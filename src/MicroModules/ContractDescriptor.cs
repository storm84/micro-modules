using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;

namespace MicroModules;

// Metadata is built once at registration, never discovered from incoming requests.
internal sealed class ContractDescriptor
{
    public Type Type { get; }
    public string Id { get; }
    public int Version { get; }
    public IReadOnlyList<OperationDescriptor> Operations { get; }

    public ContractDescriptor(Type type)
    {
        if (!type.IsInterface || !type.IsVisible || type.ContainsGenericParameters || type.GetInterfaces().Length != 0)
            throw new ArgumentException(
                $"{type.Name}: contracts must be public, non-generic interfaces without inheritance.");
        var attribute = type.GetCustomAttribute<ModuleContractAttribute>() ??
                        throw new ArgumentException($"{type.Name}: missing ModuleContract attribute.");
        ValidateId(attribute.Id);
        if (attribute.Version < 1) throw new ArgumentException($"{type.Name}: version must be positive.");
        Type = type;
        Id = attribute.Id;
        Version = attribute.Version;
        Operations = type.GetMethods().Select(method => new OperationDescriptor(method, Id, Version)).ToArray();
        if (Operations.Count == 0) throw new ArgumentException($"{type.Name}: at least one operation is required.");
        if (Operations.GroupBy(operation => operation.Id).Any(group => group.Count() > 1))
            throw new ArgumentException($"{type.Name}: operation IDs must be unique.");
        if (Operations.GroupBy(operation => operation.Method.Name).Any(group => group.Count() > 1))
            throw new ArgumentException($"{type.Name}: overloaded methods are not supported.");
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || !Regex.IsMatch(id, @"\A[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException(
                $"Invalid module/operation ID '{id}'; use lowercase letters, digits and hyphens.");
    }
}

internal sealed class OperationDescriptor
{
    public MethodInfo Method { get; }
    public string Id { get; }
    public string Path { get; }
    public bool RetrySafe { get; }
    public Type RequestType { get; }
    public Type? ResponseType { get; }
    public Func<Task<object?>, object> AdaptTask { get; }
    private readonly PropertyInfo? resultProperty;

    public OperationDescriptor(
        MethodInfo method,
        string moduleId,
        int version)
    {
        var attribute = method.GetCustomAttribute<ModuleOperationAttribute>() ??
                        throw new ArgumentException($"{method.Name}: missing ModuleOperation attribute.");
        ContractDescriptor.ValidateId(attribute.Id);
        var parameters = method.GetParameters();
        if (method.IsStatic ||
            method.IsGenericMethod ||
            method.IsSpecialName ||
            parameters.Length != 2 ||
            parameters[0].ParameterType.IsByRef ||
            parameters[0].ParameterType.IsPointer ||
            !parameters[0].ParameterType.IsClass ||
            parameters[0].ParameterType == typeof(string) ||
            parameters[1].ParameterType != typeof(CancellationToken))
            throw new ArgumentException($"{method.Name}: use one request DTO and a final CancellationToken.");
        var returnType = method.ReturnType;
        if (returnType != typeof(Task) &&
            (!returnType.IsGenericType || returnType.GetGenericTypeDefinition() != typeof(Task<>)))
            throw new ArgumentException($"{method.Name}: return Task or Task<T>.");
        Method = method;
        Id = attribute.Id;
        RetrySafe = attribute.RetrySafe;
        Path = $"/modules/{moduleId}/v{version}/{Id}";
        RequestType = parameters[0].ParameterType;
        ResponseType = returnType == typeof(Task) ? null : returnType.GenericTypeArguments[0];
        resultProperty = ResponseType is null ? null : returnType.GetProperty("Result");
        if (ResponseType is null) AdaptTask = task => task;
        else
        {
            var parameter = Expression.Parameter(typeof(Task<object?>));
            var converter = typeof(OperationDescriptor).GetMethod(
                    nameof(ConvertResultAsync),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(ResponseType);
            AdaptTask = Expression.Lambda<Func<Task<object?>, object>>(
                    Expression.Convert(Expression.Call(converter, parameter), typeof(object)),
                    parameter)
                .Compile();
        }
    }

    public async Task<object?> InvokeAsync(
        object implementation,
        object request,
        CancellationToken token)
    {
        Task task;
        try
        {
            task = (Task?)Method.Invoke(implementation, [request, token]) ??
                   throw new InvalidOperationException($"{Method.Name} returned a null Task.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }

        await task.ConfigureAwait(false);
        return resultProperty?.GetValue(task);
    }

    private static async Task<T> ConvertResultAsync<T>(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;
}