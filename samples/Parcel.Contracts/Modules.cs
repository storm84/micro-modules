using MicroModules;

namespace Parcel.Contracts;

public sealed record Address(string Name, string Street, string PostalCode, string City, string Country = "SE");
public sealed record QuoteRequest(int WeightGrams, string Service, string DestinationCountry = "SE");
public sealed record Quote(string Carrier, string Service, int WeightGrams, decimal Price, string Currency);
public sealed record BookParcelRequest(Guid OperationId, Address Sender, Address Recipient,
    string CustomerEmail, int WeightGrams, string Service);
public sealed record ShipmentRequest(Guid ShipmentId);
public sealed record ListShipmentsRequest(int Offset = 0, int Limit = 20);
public sealed record ShipmentSummary(Guid Id, string TrackingNumber, string RecipientName, string DestinationCity,
    string Status, string Service, DateTimeOffset BookedAt);
public sealed record ShipmentList(ShipmentSummary[] Shipments, bool HasMore, string ServedBy);
public sealed record SendCustomNotificationRequest(Guid OperationId, Guid ShipmentId, string Subject, string Body);
public sealed record QueuedNotification(Guid Id, Guid ShipmentId, string Email, string Subject, string Body,
    DateTimeOffset QueuedAt);

public sealed record TrackingUpdateRequest(Guid OperationId, Guid ShipmentId, string Status);
public sealed record TrackingEvent(Guid EventId, string Status, DateTimeOffset OccurredAt);
public sealed record Shipment(Guid Id, string TrackingNumber, Address Sender, Address Recipient,
    string CustomerEmail, Quote Quote, string Status, TrackingEvent[] Events);
public sealed record ShipmentResult(Shipment Shipment, string ServedBy);
public sealed record NotificationMessage(Guid Id, Guid ShipmentId, string Email, string Kind,
    string Subject, string Body, DateTimeOffset CreatedAt);
public sealed record NotificationInbox(NotificationMessage[] Messages);
public sealed record BookingDetails(Shipment Shipment, string ServedBy, NotificationMessage[] Notifications);

[ModuleContract("booking", Version = 1)]
public interface IBookingModule
{
    [ModuleOperation("quote", RetrySafe = true)]
    Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken);
    [ModuleOperation("book", RetrySafe = true)]
    Task<ShipmentResult> BookAsync(BookParcelRequest request, CancellationToken cancellationToken);
    [ModuleOperation("get-booking", RetrySafe = true)]
    Task<BookingDetails> GetBookingAsync(ShipmentRequest request, CancellationToken cancellationToken);
    [ModuleOperation("simulate-tracking", RetrySafe = true)]
    Task<ShipmentResult> SimulateTrackingAsync(TrackingUpdateRequest request, CancellationToken cancellationToken);
}

[ModuleContract("rates", Version = 1)]
public interface IRatesModule
{
    [ModuleOperation("quote", RetrySafe = true)]
    Task<Quote> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken);
}

[ModuleContract("shipments", Version = 1)]
public interface IShipmentsModule
{
    [ModuleOperation("list", RetrySafe = true)]
    Task<ShipmentList> ListAsync(ListShipmentsRequest request, CancellationToken cancellationToken);
    [ModuleOperation("send-notification", RetrySafe = true)]
    Task<QueuedNotification> SendNotificationAsync(SendCustomNotificationRequest request, CancellationToken cancellationToken);

    [ModuleOperation("book", RetrySafe = true)]
    Task<ShipmentResult> BookAsync(BookParcelRequest request, CancellationToken cancellationToken);
    [ModuleOperation("get", RetrySafe = true)]
    Task<ShipmentResult> GetAsync(ShipmentRequest request, CancellationToken cancellationToken);
    [ModuleOperation("simulate-tracking", RetrySafe = true)]
    Task<ShipmentResult> SimulateTrackingAsync(TrackingUpdateRequest request, CancellationToken cancellationToken);
}

[ModuleContract("notifications", Version = 1)]
public interface INotificationsModule
{
    [ModuleOperation("deliver", RetrySafe = true)]
    Task<NotificationMessage> DeliverAsync(NotificationMessage request, CancellationToken cancellationToken);
    [ModuleOperation("get-inbox", RetrySafe = true)]
    Task<NotificationInbox> GetInboxAsync(ShipmentRequest request, CancellationToken cancellationToken);
}
