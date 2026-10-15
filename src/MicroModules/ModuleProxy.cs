using System.Reflection;
using System.Runtime.CompilerServices;

namespace MicroModules;

public class ModuleProxy : DispatchProxy
{
    private ModuleRuntime runtime = null!;
    private ModuleRegistration registration = null!;
    private IServiceProvider provider = null!;

    internal void Initialize(
        ModuleRuntime runtime,
        ModuleRegistration registration,
        IServiceProvider provider)
        => (this.runtime, this.registration, this.provider) = (runtime, registration, provider);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) throw new ArgumentNullException(nameof(targetMethod));
        if (targetMethod.DeclaringType == typeof(object))
            return targetMethod.Name switch
            {
                nameof(ToString) => $"MicroModules proxy: {registration.Contract.Type.Name}",
                nameof(GetHashCode) => RuntimeHelpers.GetHashCode(this),
                nameof(Equals) => ReferenceEquals(this, args?[0]),
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        var operation = registration.Contract.Operations.Single(candidate => candidate.Method == targetMethod);
        var token = (CancellationToken)args![1]!;
        // Async invocation also makes synchronous implementation faults observable through the returned Task.
        return operation.AdaptTask(
            runtime.InvokeAsync(
                registration,
                operation,
                provider,
                args[0],
                token));
    }
}