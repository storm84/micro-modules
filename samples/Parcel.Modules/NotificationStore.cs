using MicroModules;
using Npgsql;
using Parcel.Contracts;
using static Parcel.Modules.ParcelJson;

namespace Parcel.Modules;

public sealed class NotificationStore(NotificationsDatabase database)
{
    private NpgsqlDataSource DataSource => database.Source;

    public async Task<NotificationMessage> DeliverAsync(NotificationMessage message, CancellationToken token)
    {
        await using var connection = await DataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO parcel_notification_inbox (id, shipment_id, payload)
            VALUES (@id, @shipment, @payload) ON CONFLICT (id) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", message.Id);
        insert.Parameters.AddWithValue("shipment", message.ShipmentId);
        AddJson(insert, "payload", message);
        await insert.ExecuteNonQueryAsync(token);
        await using var lookup = new NpgsqlCommand(
            "SELECT payload = @payload, payload::text FROM parcel_notification_inbox WHERE id = @id", connection, transaction);
        lookup.Parameters.AddWithValue("id", message.Id);
        AddJson(lookup, "payload", message);
        await using var reader = await lookup.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException("Delivered notification must exist.");
        if (!reader.GetBoolean(0))
            throw new ModuleFaultException("notification-id-conflict", "Notification ID has a different payload.", 409);
        var result = Deserialize<NotificationMessage>(reader.GetString(1));
        await reader.DisposeAsync();
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<NotificationInbox> GetInboxAsync(Guid shipmentId, CancellationToken token)
    {
        await using var command = DataSource.CreateCommand("""
            SELECT payload::text FROM parcel_notification_inbox WHERE shipment_id = @id
            ORDER BY payload->>'createdAt', id
            """);
        command.Parameters.AddWithValue("id", shipmentId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var messages = new List<NotificationMessage>();
        while (await reader.ReadAsync(token))
            messages.Add(Deserialize<NotificationMessage>(reader.GetString(0)));
        return new(messages.ToArray());
    }

}
