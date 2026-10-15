using MicroModules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Poc.Contracts;

namespace Poc.Modules;

public sealed class AModule(ICModule c) : IAModule
{
    public async Task<WorkflowResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken)
        => new("a", await c.GetCounterAsync(request, cancellationToken));

    public async Task<WorkflowResult> IncrementCounterAsync(
        IncrementCounterRequest request,
        CancellationToken cancellationToken)
        => new("a", await c.IncrementCounterAsync(request, cancellationToken));
}

public sealed class BModule(ICModule c) : IBModule
{
    public async Task<WorkflowResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken)
        => new("b", await c.GetCounterAsync(request, cancellationToken));

    public async Task<WorkflowResult> IncrementCounterAsync(
        IncrementCounterRequest request,
        CancellationToken cancellationToken)
        => new("b", await c.IncrementCounterAsync(request, cancellationToken));
}

public sealed record PocHostIdentity(string Name);

public sealed class CModule(CounterStore store, PocHostIdentity host) : ICModule
{
    public async Task<CounterResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken)
        => new(await store.GetAsync(cancellationToken), host.Name);

    public async Task<CounterResult> IncrementCounterAsync(
        IncrementCounterRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty)
            throw new ModuleFaultException("operation-id-required", "Provide a non-empty operation ID.");
        if (request.Amount is < 1 or > 1000)
            throw new ModuleFaultException("invalid-amount", "Amount must be between 1 and 1000.");
        return new(await store.IncrementAsync(request, cancellationToken), host.Name);
    }
}

public static class CounterStorageExtensions
{
    public static IServiceCollection AddCounterStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        string hostName)
    {
        var connectionString = configuration.GetConnectionString("C") ??
                               throw new InvalidOperationException("ConnectionStrings:C is required.");
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(new PocHostIdentity(hostName));
        services.AddScoped<CounterStore>();
        return services;
    }
}