using System.Net.Mail;
using System.Text.RegularExpressions;
using MicroModules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Parcel.Contracts;

namespace Parcel.Modules;

public sealed class BookingModule(IRatesModule rates, IShipmentsModule shipments,
    INotificationsModule notifications) : IBookingModule
{
    public Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken)
        => rates.QuoteAsync(request, cancellationToken);

    public Task<ShipmentResult> BookAsync(BookParcelRequest request, CancellationToken cancellationToken)
        => shipments.BookAsync(request, cancellationToken);

    public async Task<BookingDetails> GetBookingAsync(ShipmentRequest request, CancellationToken cancellationToken)
    {
        var result = await shipments.GetAsync(request, cancellationToken);
        var inbox = await notifications.GetInboxAsync(request, cancellationToken);
        return new(result.Shipment, result.ServedBy, inbox.Messages);
    }

    public Task<ShipmentResult> SimulateTrackingAsync(TrackingUpdateRequest request, CancellationToken cancellationToken)
        => shipments.SimulateTrackingAsync(request, cancellationToken);
}

public sealed class RatesModule : IRatesModule
{
    public Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DestinationCountry != "SE")
            throw new ModuleFaultException("unsupported-destination", "The demo supports domestic Swedish parcels only.");
        if (request.WeightGrams is < 1 or > 20000)
            throw new ModuleFaultException("invalid-weight", "Weight must be between 1 and 20000 grams.");
        var (carrier, basePrice) = request.Service switch
        {
            "standard" => ("Demo Parcel", 49m),
            "express" => ("Demo Express", 79m),
            _ => throw new ModuleFaultException("unknown-service", "Choose standard or express.")
        };
        return Task.FromResult(new Quote(carrier, request.Service, request.WeightGrams,
            basePrice + 10m * decimal.Ceiling(request.WeightGrams / 1000m), "SEK"));
    }
}

public sealed record SampleHostIdentity(string Name);

public sealed class ShipmentsModule(ParcelStore store, IRatesModule rates, SampleHostIdentity host) : IShipmentsModule
{
    public async Task<ShipmentResult> BookAsync(BookParcelRequest request, CancellationToken cancellationToken)
    {
        ValidateAddress(request.Sender);
        ValidateAddress(request.Recipient);
        if (!MailAddress.TryCreate(request.CustomerEmail, out var email) || email.Address != request.CustomerEmail)
            throw new ModuleFaultException("invalid-email", "Provide a customer email address.");
        var quote = await rates.QuoteAsync(new(request.WeightGrams, request.Service, request.Recipient.Country), cancellationToken);
        var shipment = await store.BookAsync(request, quote, cancellationToken);
        return new(shipment, host.Name);
    }

    public async Task<ShipmentResult> GetAsync(ShipmentRequest request, CancellationToken cancellationToken)
        => new(await store.GetAsync(request.ShipmentId, cancellationToken), host.Name);

    public async Task<ShipmentResult> SimulateTrackingAsync(TrackingUpdateRequest request, CancellationToken cancellationToken)
        => new(await store.UpdateTrackingAsync(request, cancellationToken), host.Name);

    public async Task<ShipmentList> ListAsync(ListShipmentsRequest request, CancellationToken cancellationToken)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 100)
            throw new ModuleFaultException("invalid-page", "Offset must be non-negative and limit must be between 1 and 100.");
        var items = await store.ListAsync(request, cancellationToken);
        return new(items.Take(request.Limit).ToArray(), items.Length > request.Limit, host.Name);
    }

    public Task<QueuedNotification> SendNotificationAsync(SendCustomNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200
            || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 4000)
            throw new ModuleFaultException("invalid-notification",
                "Provide a subject of up to 200 characters and a message of up to 4000 characters.");
        return store.QueueCustomNotificationAsync(request, cancellationToken);
    }

    private static void ValidateAddress(Address? address)
    {
        if (address is null || string.IsNullOrWhiteSpace(address.Name) || string.IsNullOrWhiteSpace(address.Street)
            || string.IsNullOrWhiteSpace(address.City) || address.Country != "SE"
            || address.PostalCode is null || !Regex.IsMatch(address.PostalCode, @"\A[0-9]{5}\z"))
            throw new ModuleFaultException("invalid-address", "Provide a Swedish address with a five-digit postal code.");
    }
}

public sealed class NotificationsModule(ParcelStore store) : INotificationsModule
{
    public Task<NotificationMessage> DeliverAsync(NotificationMessage request, CancellationToken cancellationToken)
    {
        if (request.Id == Guid.Empty || request.ShipmentId == Guid.Empty || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Subject)
            || string.IsNullOrWhiteSpace(request.Body))
            throw new ModuleFaultException("invalid-notification", "A complete notification and stable ID are required.");
        return store.DeliverAsync(request, cancellationToken);
    }

    public Task<NotificationInbox> GetInboxAsync(ShipmentRequest request, CancellationToken cancellationToken)
        => store.GetInboxAsync(request.ShipmentId, cancellationToken);
}

public static class ParcelStorageExtensions
{
    public static IServiceCollection AddParcelStorage(this IServiceCollection services,
        IConfiguration configuration, string hostName)
    {
        var connectionString = configuration.GetConnectionString("Parcel")
            ?? throw new InvalidOperationException("ConnectionStrings:Parcel is required.");
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(new SampleHostIdentity(hostName));
        services.AddScoped<ParcelStore>();
        if (configuration.GetValue("Outbox:Enabled", true))
            services.AddHostedService<NotificationOutboxWorker>();
        return services;
    }
}
