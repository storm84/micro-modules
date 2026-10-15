using MicroModules;
using Npgsql;
using Poc.Contracts;

namespace Poc.Modules;

public sealed class CounterStore(NpgsqlDataSource dataSource)
{
    public async Task<long> GetAsync(CancellationToken token)
    {
        await using var command = dataSource.CreateCommand("SELECT value FROM counters WHERE id = 'demo'");
        return (long)(await command.ExecuteScalarAsync(token) ??
                      throw new InvalidOperationException("Initialize the POC schema before starting the hosts."));
    }

    public async Task<long> IncrementAsync(IncrementCounterRequest request, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // A duplicate insertion waits for any in-flight transaction with this ID to finish.
        await using var reserve = new NpgsqlCommand(
            """
            INSERT INTO counter_operations (operation_id, amount)
            VALUES (@id, @amount) ON CONFLICT (operation_id) DO NOTHING
            """,
            connection,
            transaction);
        reserve.Parameters.AddWithValue("id", request.OperationId);
        reserve.Parameters.AddWithValue("amount", request.Amount);
        if (await reserve.ExecuteNonQueryAsync(token) == 0)
        {
            // A new statement under READ COMMITTED sees the winning transaction's committed result.
            await using var lookup = new NpgsqlCommand(
                "SELECT amount, result_value FROM counter_operations WHERE operation_id = @id",
                connection,
                transaction);
            lookup.Parameters.AddWithValue("id", request.OperationId);
            await using var reader = await lookup.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.IsDBNull(1))
                throw new InvalidOperationException("A committed operation must have a result.");
            if (reader.GetInt64(0) != request.Amount)
                throw new ModuleFaultException(
                    "operation-id-conflict",
                    "This operation ID was already used with a different amount.",
                    409);
            var recorded = reader.GetInt64(1);
            await reader.DisposeAsync();
            await transaction.CommitAsync(token);
            return recorded;
        }

        await using var increment = new NpgsqlCommand(
            "UPDATE counters SET value = value + @amount WHERE id = 'demo' RETURNING value",
            connection,
            transaction);
        increment.Parameters.AddWithValue("amount", request.Amount);
        var result = (long)(await increment.ExecuteScalarAsync(token) ??
                            throw new InvalidOperationException(
                                "Initialize the POC schema before starting the hosts."));
        await using var record = new NpgsqlCommand(
            "UPDATE counter_operations SET result_value = @result WHERE operation_id = @id",
            connection,
            transaction);
        record.Parameters.AddWithValue("id", request.OperationId);
        record.Parameters.AddWithValue("result", result);
        await record.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }
}