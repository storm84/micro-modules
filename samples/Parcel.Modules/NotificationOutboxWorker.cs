using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Parcel.Contracts;

namespace Parcel.Modules;

// Sample application infrastructure: MicroModules itself has no durable messaging dependency.
public sealed class NotificationOutboxWorker(IServiceScopeFactory scopes,
    ILogger<NotificationOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                var notifications = scope.ServiceProvider.GetRequiredService<INotificationsModule>();
                if (await DispatchOneAsync(dataSource, notifications, stoppingToken))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Notification outbox polling failed; retrying"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static async Task<bool> DispatchOneAsync(NpgsqlDataSource dataSource,
        INotificationsModule notifications, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var select = new NpgsqlCommand("""
            SELECT id, payload::text FROM parcel_notification_outbox
            WHERE processed_at IS NULL AND next_attempt_at <= now()
            ORDER BY next_attempt_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction);
        Guid id;
        NotificationMessage message;
        await using (var reader = await select.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
                return false;
            id = reader.GetGuid(0);
            message = ParcelStore.Deserialize<NotificationMessage>(reader.GetString(1));
        }
        string? errorMessage = null;
        try
        {
            // Inbox delivery commits independently. Replaying after an acknowledgement failure is safe.
            await notifications.DeliverAsync(message, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { errorMessage = error.Message; }
        await using var acknowledge = new NpgsqlCommand("""
            UPDATE parcel_notification_outbox SET attempts = attempts + 1,
                processed_at = CASE WHEN @error IS NULL THEN now() ELSE NULL END,
                next_attempt_at = now() + interval '2 seconds', last_error = @error
            WHERE id = @id
            """, connection, transaction);
        acknowledge.Parameters.AddWithValue("id", id);
        acknowledge.Parameters.AddWithValue("error", NpgsqlTypes.NpgsqlDbType.Text, (object?)errorMessage ?? DBNull.Value);
        await acknowledge.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }
}
