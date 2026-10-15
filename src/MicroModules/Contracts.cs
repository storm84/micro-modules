namespace MicroModules;

[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class ModuleContractAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public int Version { get; init; } = 1;
}

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ModuleOperationAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    // A declaration of application-level replay safety, not automatic deduplication.
    public bool RetrySafe { get; init; }
}

public sealed class RemoteModuleOptions
{
    public List<Uri> Endpoints { get; } = [];
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed class ModuleFaultException : Exception
{
    public ModuleFaultException(
        string code,
        string message,
        int statusCode = 400)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A fault code is required.", nameof(code));
        if (statusCode is < 400 or > 499) throw new ArgumentOutOfRangeException(nameof(statusCode));
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}

public sealed class ModuleRemoteException(
    string message,
    Uri endpoint,
    int? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public Uri Endpoint { get; } = endpoint;
    public int? StatusCode { get; } = statusCode;
}

public sealed class ModuleUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);