using System.Net;
using System.Net.Http.Json;
using MicroModules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Parcel.Contracts;
using Parcel.Modules;

namespace Parcel.Tests;

public sealed class CompositionTests
{
    private static IConfiguration Configuration(string shipments, string notifications,
        params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MicroModules:shipments:ConnectionString"] = shipments,
            ["MicroModules:shipments:Outbox:Enabled"] = "false",
            ["MicroModules:notifications:ConnectionString"] = notifications
        };
        foreach (var (key, value) in extra) settings["MicroModules:" + key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public async Task One_registration_owns_two_independent_pools_and_registers_default_dependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("test"));
        services.AddMicroModules(Configuration("Host=shipments.invalid;Database=shipments;Username=demo",
            "Host=notifications.invalid;Database=notifications;Username=demo"), modules =>
            modules.AddLocal<IShipmentsModule, ShipmentsModule>());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var shipments = provider.GetRequiredService<ShipmentsDatabase>();
        var notifications = provider.GetRequiredService<NotificationsDatabase>();
        Assert.NotSame(shipments.Source, notifications.Source);
        Assert.Equal("shipments.invalid", new NpgsqlConnectionStringBuilder(shipments.Source.ConnectionString).Host);
        Assert.Equal("notifications.invalid", new NpgsqlConnectionStringBuilder(notifications.Source.ConnectionString).Host);
        Assert.Null(provider.GetService<NpgsqlDataSource>());
        await using var scope = provider.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INotificationsModule>());
        Assert.Equal(59, (await scope.ServiceProvider.GetRequiredService<IRatesModule>()
            .QuoteAsync(new(100, "standard"), default)).Price);
    }

    [Fact]
    public void Remote_root_ignores_invalid_database_settings_and_registers_no_worker_or_pool()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Configuration("secret-invalid-connection", "secret-invalid-connection",
            ("shipments:Mode", "Remote"), ("shipments:Endpoints:0", "http://remote")), modules =>
            modules.AddModule<IShipmentsModule, ShipmentsModule>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ShipmentsDatabase)
            || descriptor.ServiceType == typeof(NotificationsDatabase) || descriptor.ServiceType == typeof(IHostedService)
            || descriptor.ServiceType == typeof(IRatesModule));
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret-invalid-connection")]
    public void Invalid_local_settings_identify_the_module_without_leaking_values(string connection)
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(
            Configuration(connection, "Host=localhost;Username=demo"), modules =>
                modules.AddLocal<IShipmentsModule, ShipmentsModule>()));
        Assert.Contains("MicroModules:shipments:ConnectionString", error.Message);
        Assert.DoesNotContain("secret-invalid-connection", error.ToString());
    }

    [DatabaseFact]
    public async Task One_local_root_books_with_local_rates_and_delivers_across_independent_storage()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("composition"));
        services.AddMicroModules(Configuration(database.ShipmentsConnectionString,
            database.NotificationsConnectionString), modules => modules.AddLocal<IShipmentsModule, ShipmentsModule>());
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RejectHttpHandler());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var result = await scope.ServiceProvider.GetRequiredService<IShipmentsModule>().BookAsync(
            new(Guid.NewGuid(), address, address, "alex@example.test", 1500, "standard"), default);
        Assert.Equal("composition", result.ServedBy);
        Assert.Equal(69, result.Shipment.Quote.Price);
        Assert.True(await NotificationOutboxWorker.DispatchOneAsync(provider.GetRequiredService<ShipmentsDatabase>().Source,
            scope.ServiceProvider.GetRequiredService<INotificationsModule>(), default));
        Assert.Single((await scope.ServiceProvider.GetRequiredService<INotificationsModule>()
            .GetInboxAsync(new(result.Shipment.Id), default)).Messages);
        // These schemas contain only their owner's tables, so accidental cross-module queries fail.
        await using var shipmentTables = database.Source.CreateCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'parcel_notification_inbox'");
        Assert.Equal(0L, await shipmentTables.ExecuteScalarAsync());
        await using var notificationTables = database.Notifications.Source.CreateCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'parcel_shipments'");
        Assert.Equal(0L, await notificationTables.ExecuteScalarAsync());
    }

    [DatabaseFact]
    public async Task Configuration_can_replace_the_default_local_rates_with_remote_rates()
    {
        await using var database = await IsolatedDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment("composition"));
        services.AddMicroModules(Configuration(database.ShipmentsConnectionString,
            database.NotificationsConnectionString,
            ("rates:Mode", "Remote"), ("rates:Endpoints:0", "http://rates")), modules =>
                modules.AddLocal<IShipmentsModule, ShipmentsModule>());
        var handler = new RatesHandler();
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var address = new Address("Alex", "Example Street 1", "11122", "Stockholm");
        var result = await scope.ServiceProvider.GetRequiredService<IShipmentsModule>().BookAsync(
            new(Guid.NewGuid(), address, address, "alex@example.test", 1500, "standard"), default);
        Assert.Equal(123, result.Shipment.Quote.Price);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(RatesModule));
    }

    private sealed class RejectHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new Xunit.Sdk.XunitException("Local sample modules must not use HTTP.");
    }

    private sealed class RatesHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("http://rates/modules/rates/v1/quote", request.RequestUri!.ToString());
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new Quote("Remote Demo", "standard", 1500, 123, "SEK"))
            });
        }
    }
}
