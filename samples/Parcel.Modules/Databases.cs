using MicroModules;
using Npgsql;

namespace Parcel.Modules;

// Distinct DI types prevent one module's pool from replacing another's.
public sealed class ShipmentsDatabase(string connectionString) : IDisposable, IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(connectionString);
    public void Dispose() => Source.Dispose();
    public ValueTask DisposeAsync() => Source.DisposeAsync();
}

public sealed class NotificationsDatabase(string connectionString) : IDisposable, IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = NpgsqlDataSource.Create(connectionString);
    public void Dispose() => Source.Dispose();
    public ValueTask DisposeAsync() => Source.DisposeAsync();
}

internal static class DatabaseSettings
{
    public static string ConnectionString(ModuleSetupContext module)
    {
        var path = module.Configuration.Path + ":ConnectionString";
        var value = module.Configuration["ConnectionString"];
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{path} is required for local execution.");
        try { _ = new NpgsqlConnectionStringBuilder(value); }
        catch (ArgumentException) { throw new ArgumentException($"{path} is not a valid Postgres connection string."); }
        return value;
    }
}
