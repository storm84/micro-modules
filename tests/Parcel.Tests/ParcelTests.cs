using MicroModules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Parcel.Contracts;
using Parcel.Modules;

namespace Parcel.Tests;

public sealed class ParcelTests
{
    [Theory]
    [InlineData(1, "standard", 59)]
    [InlineData(1000, "standard", 59)]
    [InlineData(1001, "standard", 69)]
    [InlineData(20000, "express", 279)]
    public async Task Quotes_use_whole_started_kilograms(int grams, string service, int price)
    {
        var quote = await new RatesModule().QuoteAsync(new(grams, service), CancellationToken.None);
        Assert.Equal(price, quote.Price);
        Assert.Equal("SEK", quote.Currency);
    }

    [Theory]
    [InlineData(0, "standard", "SE", "invalid-weight")]
    [InlineData(20001, "standard", "SE", "invalid-weight")]
    [InlineData(1, "other", "SE", "unknown-service")]
    [InlineData(1, "standard", "NO", "unsupported-destination")]
    public async Task Quotes_reject_invalid_parcels(int grams, string service, string country, string code)
    {
        var error = await Assert.ThrowsAsync<ModuleFaultException>(() =>
            new RatesModule().QuoteAsync(new(grams, service, country), CancellationToken.None));
        Assert.Equal(code, error.Code);
    }

    [DatabaseFact]
    public async Task Outbox_retries_delivery_and_replays_after_delivery_before_acknowledgement()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var source = database.Source;
        var store = new ShipmentStore(database.Shipments);
        var shipments = new ShipmentsModule(store, new RatesModule(), new TestEnvironment("test"));
        var inbox = new NotificationsModule(new NotificationStore(database.Notifications));
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var booking = new BookParcelRequest(Guid.NewGuid(), address, address, "alex@example.test", 1500, "standard");
        var shipment = (await shipments.BookAsync(booking, CancellationToken.None)).Shipment;
        var notificationId = booking.OperationId;

        // Failure before delivery leaves a durable pending row with retry information.
        Assert.True(await NotificationOutboxWorker.DispatchOneAsync(source,
            new FaultingNotifications(inbox, afterDelivery: false), CancellationToken.None));
        Assert.Empty((await inbox.GetInboxAsync(new(shipment.Id), CancellationToken.None)).Messages);
        await AssertOutboxAsync(source, notificationId, 1, false);
        await MakeDueAsync(source, notificationId);

        // The inbox commits, but acknowledgement fails. The next dispatcher must deduplicate delivery.
        Assert.True(await NotificationOutboxWorker.DispatchOneAsync(source,
            new FaultingNotifications(inbox, afterDelivery: true), CancellationToken.None));
        Assert.Single((await inbox.GetInboxAsync(new(shipment.Id), CancellationToken.None)).Messages);
        await AssertOutboxAsync(source, notificationId, 2, false);
        await MakeDueAsync(source, notificationId);
        var dispatched = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            NotificationOutboxWorker.DispatchOneAsync(source, inbox, CancellationToken.None)));
        Assert.Single(dispatched, value => value);
        await AssertOutboxAsync(source, notificationId, 3, true);
        var messages = (await inbox.GetInboxAsync(new(shipment.Id), CancellationToken.None)).Messages;
        var message = Assert.Single(messages);
        Assert.Equal(notificationId, message.Id);
        Assert.Equal("booking-confirmation", message.Kind);
        await Assert.ThrowsAsync<ModuleFaultException>(() => inbox.DeliverAsync(
            message with { Body = "Changed payload" }, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Shutdown_after_delivery_rolls_back_acknowledgement_and_releases_the_row_lock()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var store = new ShipmentStore(database.Shipments);
        var module = new ShipmentsModule(store, new RatesModule(), new TestEnvironment("test"));
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var booking = new BookParcelRequest(Guid.NewGuid(), address, address, "alex@example.test", 100, "standard");
        var shipment = (await module.BookAsync(booking, CancellationToken.None)).Shipment;
        var inbox = new NotificationsModule(new NotificationStore(database.Notifications));
        using var shutdown = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NotificationOutboxWorker.DispatchOneAsync(database.Source,
                new ShutdownAfterDelivery(inbox, shutdown), shutdown.Token));
        // Cancellation rolls back the dispatch transaction, including its lock and attempt count.
        await using (var command = database.Source.CreateCommand("""
            SELECT attempts, processed_at IS NULL, last_error IS NULL FROM parcel_notification_outbox WHERE id = @id
            """))
        {
            command.Parameters.AddWithValue("id", booking.OperationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0, reader.GetInt32(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }
        Assert.True(await NotificationOutboxWorker.DispatchOneAsync(database.Source, inbox, CancellationToken.None));
        Assert.Single((await inbox.GetInboxAsync(new(shipment.Id), CancellationToken.None)).Messages);
        await AssertOutboxAsync(database.Source, booking.OperationId, 1, true);
    }

    [DatabaseFact]
    public async Task Competing_transitions_commit_one_event_and_failed_operations_can_be_corrected()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var store = new ShipmentStore(database.Shipments);
        var module = new ShipmentsModule(store, new RatesModule(), new TestEnvironment("test"));
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var booking = new BookParcelRequest(Guid.NewGuid(), address, address, "alex@example.test", 100, "standard");
        var shipment = (await module.BookAsync(booking, CancellationToken.None)).Shipment;
        var update = new TrackingUpdateRequest(Guid.NewGuid(), shipment.Id, "Delivered");
        await Assert.ThrowsAsync<ModuleFaultException>(() => module.SimulateTrackingAsync(update, CancellationToken.None));
        // The failed transaction rolled back both its operation reservation and side effects.
        update = update with { Status = "Collected" };
        var requests = new[] { update, update with { OperationId = Guid.NewGuid() } };
        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            try { await module.SimulateTrackingAsync(request, CancellationToken.None); return true; }
            catch (ModuleFaultException error) when (error.Code == "invalid-transition") { return false; }
        }));
        Assert.Single(outcomes, value => value);
        var current = (await module.GetAsync(new(shipment.Id), CancellationToken.None)).Shipment;
        Assert.Equal(new[] { "Booked", "Collected" }, current.Events.Select(value => value.Status));
        Assert.All(current.Events, value => Assert.Equal(TimeSpan.Zero, value.OccurredAt.Offset));
        await using var command = database.Source.CreateCommand("SELECT count(*) FROM parcel_notification_outbox");
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task Listing_validates_page_bounds_before_accessing_storage(int offset, int limit)
    {
        await using var source = new ShipmentsDatabase("Host=localhost;Database=unused;Username=unused");
        var module = new ShipmentsModule(new(source), new RatesModule(), new TestEnvironment("test"));
        var error = await Assert.ThrowsAsync<ModuleFaultException>(() => module.ListAsync(new(offset, limit), CancellationToken.None));
        Assert.Equal("invalid-page", error.Code);
    }

    [Theory]
    [InlineData("", "Message")]
    [InlineData("Subject", " ")]
    [InlineData(null, "Message")]
    public async Task Custom_notifications_validate_content_before_accessing_storage(string? subject, string body)
    {
        await using var source = new ShipmentsDatabase("Host=localhost;Database=unused;Username=unused");
        var module = new ShipmentsModule(new(source), new RatesModule(), new TestEnvironment("test"));
        var error = await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), subject!, body), CancellationToken.None));
        Assert.Equal("invalid-notification", error.Code);
        await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('x', 201), "Message"), CancellationToken.None));
        await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "Subject", new string('x', 4001)), CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Listing_pages_bookings_and_custom_notifications_use_the_durable_replay_path()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var store = new ShipmentStore(database.Shipments);
        var module = new ShipmentsModule(store, new RatesModule(), new TestEnvironment("test"));
        Assert.Empty((await module.ListAsync(new(), CancellationToken.None)).Shipments);
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var booking = new BookParcelRequest(Guid.NewGuid(), address, address, "alex@example.test", 100, "standard");
        var original = (await module.BookAsync(booking, CancellationToken.None)).Shipment;
        await module.BookAsync(booking with { OperationId = Guid.NewGuid() }, CancellationToken.None);
        await module.BookAsync(booking with { OperationId = Guid.NewGuid() }, CancellationToken.None);
        var first = await module.ListAsync(new(0, 2), CancellationToken.None);
        var second = await module.ListAsync(new(2, 2), CancellationToken.None);
        Assert.Equal(2, first.Shipments.Length);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal("test", first.ServedBy);
        Assert.Equal(original.Id, Assert.Single(second.Shipments).Id);
        Assert.Equal(3, first.Shipments.Concat(second.Shipments).Select(value => value.Id).Distinct().Count());

        var request = new SendCustomNotificationRequest(Guid.NewGuid(), original.Id, "Parcel update", "A custom message.");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => module.SendNotificationAsync(request, CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(booking.CustomerEmail, results[0].Email);
        Assert.Equal(request.OperationId, results[0].Id);
        var conflict = await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            request with { Body = "Changed payload" }, CancellationToken.None));
        Assert.Equal("operation-id-conflict", conflict.Code);
        var missing = await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            request with { OperationId = Guid.NewGuid(), ShipmentId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("shipment-not-found", missing.Code);
        await Assert.ThrowsAsync<ModuleFaultException>(() => module.SendNotificationAsync(
            request with { OperationId = Guid.Empty }, CancellationToken.None));
        Assert.Single((await module.GetAsync(new(original.Id), CancellationToken.None)).Shipment.Events);
        var inbox = new NotificationsModule(new NotificationStore(database.Notifications));
        while (await NotificationOutboxWorker.DispatchOneAsync(database.Source, inbox, CancellationToken.None)) { }
        var messages = (await inbox.GetInboxAsync(new(original.Id), CancellationToken.None)).Messages;
        Assert.Equal(2, messages.Length);
        var custom = Assert.Single(messages, message => message.Kind == "custom");
        Assert.Equal(request.Body, custom.Body);
        Assert.Equal(booking.CustomerEmail, custom.Email);
        await using var count = database.Source.CreateCommand("SELECT count(*) FROM parcel_notification_outbox");
        Assert.Equal(4L, await count.ExecuteScalarAsync());
    }

    private static async Task MakeDueAsync(NpgsqlDataSource source, Guid id)
    {
        await using var command = source.CreateCommand(
            "UPDATE parcel_notification_outbox SET next_attempt_at = now() WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOutboxAsync(NpgsqlDataSource source, Guid id, int attempts, bool processed)
    {
        await using var command = source.CreateCommand("""
            SELECT attempts, processed_at IS NOT NULL, last_error FROM parcel_notification_outbox WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(attempts, reader.GetInt32(0));
        Assert.Equal(processed, reader.GetBoolean(1));
        Assert.Equal(processed, reader.IsDBNull(2));
    }

    private sealed class ShutdownAfterDelivery(INotificationsModule inner, CancellationTokenSource shutdown)
        : INotificationsModule
    {
        public async Task<NotificationMessage> DeliverAsync(NotificationMessage request, CancellationToken token)
        {
            await inner.DeliverAsync(request, token);
            await shutdown.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected shutdown cancellation.");
        }

        public Task<NotificationInbox> GetInboxAsync(ShipmentRequest request, CancellationToken token)
            => inner.GetInboxAsync(request, token);
    }

    private sealed class FaultingNotifications(INotificationsModule inner, bool afterDelivery) : INotificationsModule
    {
        public async Task<NotificationMessage> DeliverAsync(NotificationMessage request, CancellationToken token)
        {
            if (afterDelivery) await inner.DeliverAsync(request, token);
            throw new IOException(afterDelivery ? "Acknowledgement lost" : "Delivery unavailable");
        }

        public Task<NotificationInbox> GetInboxAsync(ShipmentRequest request, CancellationToken token)
            => inner.GetInboxAsync(request, token);
    }
}

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PARCEL_TEST_CONNECTION")))
            Skip = "Run scripts/poc.py smoke or set PARCEL_TEST_CONNECTION to a local Postgres database.";
    }
}

internal sealed class TestEnvironment(string name) : IHostEnvironment
{
    public string ApplicationName { get; set; } = name;
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class IsolatedDatabase(ShipmentsDatabase shipments, NotificationsDatabase notifications,
    string connectionString, string schemaPrefix) : IAsyncDisposable
{
    public ShipmentsDatabase Shipments { get; } = shipments;
    public NotificationsDatabase Notifications { get; } = notifications;
    public NpgsqlDataSource Source => Shipments.Source;
    // NpgsqlDataSource.ConnectionString intentionally omits the password. Retain fixture inputs for new pools.
    public string ShipmentsConnectionString => new NpgsqlConnectionStringBuilder(connectionString)
        { SearchPath = schemaPrefix + "_shipments" }.ConnectionString;
    public string NotificationsConnectionString => new NpgsqlConnectionStringBuilder(connectionString)
        { SearchPath = schemaPrefix + "_notifications" }.ConnectionString;

    public static async Task<IsolatedDatabase> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("PARCEL_TEST_CONNECTION")!;
        var schema = "parcel_test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(connectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}_shipments; CREATE SCHEMA {schema}_notifications"))
            await create.ExecuteNonQueryAsync();
        var shipments = new ShipmentsDatabase(new NpgsqlConnectionStringBuilder(connectionString)
            { SearchPath = schema + "_shipments" }.ConnectionString);
        var notifications = new NotificationsDatabase(new NpgsqlConnectionStringBuilder(connectionString)
            { SearchPath = schema + "_notifications" }.ConnectionString);
        var database = new IsolatedDatabase(shipments, notifications, connectionString, schema);
        try
        {
            await using var initializeShipments = shipments.Source.CreateCommand(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "001-shipments.sql")));
            await initializeShipments.ExecuteNonQueryAsync();
            await using var initializeNotifications = notifications.Source.CreateCommand(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "002-notifications.sql")));
            await initializeNotifications.ExecuteNonQueryAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await Shipments.DisposeAsync();
        await Notifications.DisposeAsync();
        await using var admin = NpgsqlDataSource.Create(connectionString);
        // Only the randomly named schemas created by this fixture are removed.
        await using var drop = admin.CreateCommand($"DROP SCHEMA {schemaPrefix}_shipments CASCADE; DROP SCHEMA {schemaPrefix}_notifications CASCADE");
        await drop.ExecuteNonQueryAsync();
    }
}
