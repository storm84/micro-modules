using MicroModules;
using Npgsql;
using Parcel.Contracts;
using static Parcel.Modules.ParcelJson;

namespace Parcel.Modules;

public sealed class ShipmentStore(ShipmentsDatabase database)
{
    private NpgsqlDataSource DataSource => database.Source;

    public Task<Shipment> BookAsync(BookParcelRequest request, Quote quote, CancellationToken token)
        => ExecuteOnceAsync(request.OperationId, "book", request, async (connection, transaction) =>
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var shipment = new Shipment(id, $"DEMO-{id:N}", request.Sender, request.Recipient,
                request.CustomerEmail, quote, "Booked", [new(request.OperationId, "Booked", now)]);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO parcel_shipments (id, payload) VALUES (@id, @payload)", connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            AddJson(insert, "payload", shipment);
            await insert.ExecuteNonQueryAsync(token);
            await EnqueueAsync(connection, transaction, shipment, request.OperationId, "booking-confirmation", now, token);
            return shipment;
        }, token);

    public Task<Shipment> UpdateTrackingAsync(TrackingUpdateRequest request, CancellationToken token)
        => ExecuteOnceAsync(request.OperationId, "tracking", request, async (connection, transaction) =>
        {
            var shipment = await ReadShipmentAsync(connection, transaction, request.ShipmentId, true, token);
            var next = shipment.Status switch
            {
                "Booked" => "Collected",
                "Collected" => "InTransit",
                "InTransit" => "Delivered",
                _ => null
            };
            if (next is null || request.Status != next)
                throw new ModuleFaultException("invalid-transition",
                    $"Cannot change {shipment.Status} to {request.Status}. Expected {next ?? "no further updates"}.", 409);
            var now = DateTimeOffset.UtcNow;
            var updated = shipment with
            {
                Status = next,
                Events = [.. shipment.Events, new(request.OperationId, next, now)]
            };
            await using var update = new NpgsqlCommand(
                "UPDATE parcel_shipments SET payload = @payload WHERE id = @id", connection, transaction);
            update.Parameters.AddWithValue("id", request.ShipmentId);
            AddJson(update, "payload", updated);
            await update.ExecuteNonQueryAsync(token);
            if (next == "Delivered")
                await EnqueueAsync(connection, transaction, updated, request.OperationId, "delivered", now, token);
            return updated;
        }, token);

    public async Task<Shipment> GetAsync(Guid id, CancellationToken token)
    {
        await using var connection = await DataSource.OpenConnectionAsync(token);
        return await ReadShipmentAsync(connection, null, id, false, token);
    }

    public async Task<ShipmentSummary[]> ListAsync(ListShipmentsRequest request, CancellationToken token)
    {
        await using var command = DataSource.CreateCommand("""
            SELECT payload::text FROM parcel_shipments
            ORDER BY payload->'events'->0->>'occurredAt' DESC, id DESC
            OFFSET @offset LIMIT @limit
            """);
        command.Parameters.AddWithValue("offset", request.Offset);
        command.Parameters.AddWithValue("limit", request.Limit + 1);
        var items = new List<ShipmentSummary>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var shipment = Deserialize<Shipment>(reader.GetString(0));
            items.Add(new(shipment.Id, shipment.TrackingNumber, shipment.Recipient.Name, shipment.Recipient.City,
                shipment.Status, shipment.Quote.Service, shipment.Events[0].OccurredAt));
        }
        return items.ToArray();
    }

    public Task<QueuedNotification> QueueCustomNotificationAsync(SendCustomNotificationRequest request, CancellationToken token)
        => ExecuteOnceAsync(request.OperationId, "custom-notification", request, async (connection, transaction) =>
        {
            var shipment = await ReadShipmentAsync(connection, transaction, request.ShipmentId, true, token);
            var now = DateTimeOffset.UtcNow;
            var message = new NotificationMessage(request.OperationId, shipment.Id, shipment.CustomerEmail,
                "custom", request.Subject, request.Body, now);
            await InsertOutboxAsync(connection, transaction, message, token);
            return new QueuedNotification(message.Id, shipment.Id, message.Email, message.Subject, message.Body, now);
        }, token);

    private static async Task<Shipment> ReadShipmentAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, Guid id, bool forUpdate, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM parcel_shipments WHERE id = @id" + (forUpdate ? " FOR UPDATE" : ""),
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        var payload = await command.ExecuteScalarAsync(token) as string;
        return payload is null
            ? throw new ModuleFaultException("shipment-not-found", "No shipment exists with that ID.", 404)
            : Deserialize<Shipment>(payload);
    }

    private async Task<T> ExecuteOnceAsync<T>(Guid operationId, string kind, object request,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, CancellationToken token) where T : notnull
    {
        if (operationId == Guid.Empty)
            throw new ModuleFaultException("operation-id-required", "Provide a non-empty operation ID.");
        await using var connection = await DataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // An overlapping duplicate waits for the winner to commit or roll back.
        await using var reserve = new NpgsqlCommand("""
            INSERT INTO parcel_operations (operation_id, kind, request)
            VALUES (@id, @kind, @request) ON CONFLICT (operation_id) DO NOTHING
            """, connection, transaction);
        reserve.Parameters.AddWithValue("id", operationId);
        reserve.Parameters.AddWithValue("kind", kind);
        AddJson(reserve, "request", request);
        if (await reserve.ExecuteNonQueryAsync(token) == 0)
        {
            await using var lookup = new NpgsqlCommand("""
                SELECT kind = @kind AND request = @request, result::text
                FROM parcel_operations WHERE operation_id = @id
                """, connection, transaction);
            lookup.Parameters.AddWithValue("id", operationId);
            lookup.Parameters.AddWithValue("kind", kind);
            AddJson(lookup, "request", request);
            await using var reader = await lookup.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.IsDBNull(1))
                throw new InvalidOperationException("A committed operation must have a result.");
            if (!reader.GetBoolean(0))
                throw new ModuleFaultException("operation-id-conflict",
                    "This operation ID was already used for a different request.", 409);
            var recorded = Deserialize<T>(reader.GetString(1));
            await reader.DisposeAsync();
            await transaction.CommitAsync(token);
            return recorded;
        }
        var result = await action(connection, transaction);
        await using var record = new NpgsqlCommand(
            "UPDATE parcel_operations SET result = @result WHERE operation_id = @id", connection, transaction);
        record.Parameters.AddWithValue("id", operationId);
        AddJson(record, "result", result);
        await record.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private static async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Shipment shipment, Guid eventId, string kind, DateTimeOffset now, CancellationToken token)
    {
        var subject = kind == "booking-confirmation" ? "Your parcel is booked" : "Your parcel has been delivered";
        var body = kind == "booking-confirmation"
            ? $"Track {shipment.TrackingNumber}. {shipment.Quote.Carrier}: {shipment.Quote.Price:0.00} {shipment.Quote.Currency}."
            : $"Parcel {shipment.TrackingNumber} has been delivered to {shipment.Recipient.Name}.";
        var message = new NotificationMessage(eventId, shipment.Id, shipment.CustomerEmail, kind, subject, body, now);
        await InsertOutboxAsync(connection, transaction, message, token);
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        NotificationMessage message, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO parcel_notification_outbox (id, payload) VALUES (@id, @payload)", connection, transaction);
        command.Parameters.AddWithValue("id", message.Id);
        AddJson(command, "payload", message);
        await command.ExecuteNonQueryAsync(token);
    }

}
