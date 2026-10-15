using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MicroModules;

internal sealed class ModuleRegistration(ContractDescriptor contract)
{
    public ContractDescriptor Contract { get; } = contract;
    public Type? ImplementationType { get; set; }
    public RemoteModuleOptions? Remote { get; set; }
}

internal sealed class ModuleRegistry
{
    public Dictionary<Type, ModuleRegistration> Registrations { get; } = [];

    public ModuleRegistration GetOrAdd(Type type)
    {
        if (Registrations.TryGetValue(type, out var existing)) return existing;
        var descriptor = new ContractDescriptor(type);
        if (Registrations.Values.Any(registration
                => registration.Contract.Id == descriptor.Id && registration.Contract.Version == descriptor.Version))
            throw new ArgumentException($"Duplicate module identity: {descriptor.Id}/v{descriptor.Version}.");
        var registration = new ModuleRegistration(descriptor);
        Registrations.Add(type, registration);
        return registration;
    }
}

public sealed class MicroModuleBuilder
{
    private readonly IServiceCollection services;
    private readonly ModuleRegistry registry;
    private bool completed;

    internal MicroModuleBuilder(IServiceCollection services, ModuleRegistry registry)
        => (this.services, this.registry) = (services, registry);

    public MicroModuleBuilder AddLocal<TContract, TImplementation>() where TContract : class
        where TImplementation : class, TContract
    {
        EnsureMutable();
        var registration = registry.GetOrAdd(typeof(TContract));
        if (registration.ImplementationType is not null)
            throw new ArgumentException($"{typeof(TContract).Name}: duplicate local implementation.");
        registration.ImplementationType = typeof(TImplementation);
        services.TryAddScoped<TImplementation>();
        return this;
    }

    public MicroModuleBuilder AddRemote<TContract>(Action<RemoteModuleOptions> configure) where TContract : class
    {
        EnsureMutable();
        var options = new RemoteModuleOptions();
        configure(options);
        if (options.Endpoints.Count == 0 ||
            options.Endpoints.Any(endpoint
                => !endpoint.IsAbsoluteUri ||
                   endpoint.Scheme is not ("http" or "https") ||
                   endpoint.Query.Length != 0 ||
                   endpoint.Fragment.Length != 0 ||
                   endpoint.UserInfo.Length != 0))
            throw new ArgumentException(
                "Provide at least one absolute HTTP(S) endpoint without query, fragment or credentials.");
        if (options.Endpoints.Distinct().Count() != options.Endpoints.Count)
            throw new ArgumentException("Remote endpoints must be unique.");
        if (options.AttemptTimeout <= TimeSpan.Zero ||
            options.TotalTimeout <= TimeSpan.Zero ||
            options.AttemptTimeout.TotalMilliseconds > uint.MaxValue - 1 ||
            options.TotalTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException("Remote timeouts must be positive and within CancellationTokenSource limits.");
        var registration = registry.GetOrAdd(typeof(TContract));
        if (registration.Remote is not null)
            throw new ArgumentException($"{typeof(TContract).Name}: duplicate remote registration.");
        // Snapshot caller-owned mutable options.
        registration.Remote = new RemoteModuleOptions
        {
            AttemptTimeout = options.AttemptTimeout,
            TotalTimeout = options.TotalTimeout
        };
        registration.Remote.Endpoints.AddRange(options.Endpoints);
        return this;
    }

    public MicroModuleBuilder AddRemote<TContract>(IConfiguration configuration) where TContract : class
        => AddRemote<TContract>(options =>
        {
            foreach (var endpoint in configuration.GetSection("Endpoints").GetChildren())
                options.Endpoints.Add(
                    new Uri(
                        endpoint.Value ?? throw new ArgumentException("Endpoint cannot be null."),
                        UriKind.Absolute));
            if (configuration["AttemptTimeoutSeconds"] is { } attempt)
                options.AttemptTimeout = TimeSpan.FromSeconds(
                    double.Parse(attempt, System.Globalization.CultureInfo.InvariantCulture));
            if (configuration["TotalTimeoutSeconds"] is { } total)
                options.TotalTimeout =
                    TimeSpan.FromSeconds(double.Parse(total, System.Globalization.CultureInfo.InvariantCulture));
        });

    internal void Complete() => completed = true;

    private void EnsureMutable()
    {
        if (completed)
            throw new InvalidOperationException("Module registrations cannot change after AddMicroModules completes.");
    }
}

public static class MicroModuleServiceCollectionExtensions
{
    public const string HttpClientName = "MicroModules";

    public static IServiceCollection AddMicroModules(
        this IServiceCollection services,
        Action<MicroModuleBuilder> configure)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleRegistry)))
            throw new InvalidOperationException(
                "Call AddMicroModules once per host and compose all registrations inside it.");
        var registry = new ModuleRegistry();
        var builder = new MicroModuleBuilder(services, registry);
        configure(builder);
        builder.Complete();
        services.AddSingleton(registry);
        services.AddLogging();
        services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<ModuleRuntime>();
        foreach (var registration in registry.Registrations.Values)
        {
            services.AddScoped(
                registration.Contract.Type,
                provider =>
                {
                    var proxy = (ModuleProxy)System.Reflection.DispatchProxy.Create(
                        registration.Contract.Type,
                        typeof(ModuleProxy));
                    proxy.Initialize(provider.GetRequiredService<ModuleRuntime>(), registration, provider);
                    return proxy;
                });
        }

        return services;
    }
}