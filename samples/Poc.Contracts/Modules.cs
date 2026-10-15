using MicroModules;

namespace Poc.Contracts;

public sealed record GetCounterRequest;

public sealed record IncrementCounterRequest(Guid OperationId, long Amount = 1);

public sealed record CounterResult(long Value, string ServedBy);

public sealed record WorkflowResult(string Module, CounterResult Counter);

[ModuleContract("c", Version = 1)]
public interface ICModule
{
    [ModuleOperation("get-counter", RetrySafe = true)]
    Task<CounterResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken);

    // Both C deployments persist the operation ID and result atomically with the write.
    [ModuleOperation("increment-counter", RetrySafe = true)]
    Task<CounterResult> IncrementCounterAsync(IncrementCounterRequest request, CancellationToken cancellationToken);
}

[ModuleContract("a", Version = 1)]
public interface IAModule
{
    [ModuleOperation("get-counter", RetrySafe = true)]
    Task<WorkflowResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken);

    [ModuleOperation("increment-counter", RetrySafe = true)]
    Task<WorkflowResult> IncrementCounterAsync(IncrementCounterRequest request, CancellationToken cancellationToken);
}

[ModuleContract("b", Version = 1)]
public interface IBModule
{
    [ModuleOperation("get-counter", RetrySafe = true)]
    Task<WorkflowResult> GetCounterAsync(GetCounterRequest request, CancellationToken cancellationToken);

    [ModuleOperation("increment-counter", RetrySafe = true)]
    Task<WorkflowResult> IncrementCounterAsync(IncrementCounterRequest request, CancellationToken cancellationToken);
}