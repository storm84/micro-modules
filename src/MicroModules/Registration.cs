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
    private sealed class Definition(ModuleRegistration registration)
    {
        public ModuleRegistration Registration { get; } = registration;
        public Type? Local { get; set; }
        public Type? Configured { get; set; }
        public Type? Inferred { get; set; }
        public RemoteModuleOptions? Remote { get; set; }
        public bool Resolved { get; set; }
    }

    private readonly IServiceCollection services;
    private readonly ModuleRegistry registry;
    private readonly IConfiguration configuration;
    private readonly Dictionary<Type, Definition> definitions = [];
    private readonly List<Type> resolving = [];
    private bool completed;

    internal MicroModuleBuilder(IServiceCollection services, ModuleRegistry registry, IConfiguration configuration)
        => (this.services, this.registry, this.configuration) = (services, registry, configuration.GetSection("MicroModules"));

    public MicroModuleBuilder AddLocal<TContract, TImplementation>() where TContract : class
        where TImplementation : class, TContract
    {
        EnsureMutable();
        var definition = GetDefinition(typeof(TContract));
        if (definition.Local is not null)
            throw new ArgumentException($"{typeof(TContract).Name}: duplicate local implementation.");
        definition.Local = typeof(TImplementation);
        return this;
    }

    /// <summary>Selects local (default) or remote execution from this module's configuration.</summary>
    public MicroModuleBuilder AddModule<TContract, TImplementation>() where TContract : class
        where TImplementation : class, TContract
    {
        EnsureMutable();
        var definition = GetDefinition(typeof(TContract));
        if (definition.Configured is not null)
            throw new ArgumentException($"{typeof(TContract).Name}: duplicate configurable registration.");
        definition.Configured = typeof(TImplementation);
        return this;
    }

    public MicroModuleBuilder AddRemote<TContract>() where TContract : class
    {
        EnsureMutable();
        return AddRemote<TContract>(Section(GetDefinition(typeof(TContract))));
    }

    public MicroModuleBuilder AddRemote<TContract>(Action<RemoteModuleOptions> configure) where TContract : class
    {
        EnsureMutable();
        var definition = GetDefinition(typeof(TContract));
        var options = new RemoteModuleOptions();
        configure(options);
        SetRemote(definition, options);
        return this;
    }

    public MicroModuleBuilder AddRemote<TContract>(IConfiguration configuration) where TContract : class
    {
        EnsureMutable();
        SetRemote(GetDefinition(typeof(TContract)), ReadRemote(configuration));
        return this;
    }

    private static void SetRemote(Definition definition, RemoteModuleOptions options)
    {
        ValidateRemote(options, definition.Registration.Contract.Id);
        if (definition.Remote is not null)
            throw new ArgumentException($"{definition.Registration.Contract.Type.Name}: duplicate remote registration.");
        // Snapshot caller-owned mutable options.
        definition.Remote = new RemoteModuleOptions
        {
            AttemptTimeout = options.AttemptTimeout,
            TotalTimeout = options.TotalTimeout
        };
        definition.Remote.Endpoints.AddRange(options.Endpoints);
    }

    private Definition GetDefinition(Type contract)
    {
        if (definitions.TryGetValue(contract, out var existing)) return existing;
        var definition = new Definition(registry.GetOrAdd(contract));
        definitions.Add(contract, definition);
        return definition;
    }

    private IConfigurationSection Section(Definition definition)
        => configuration.GetSection(definition.Registration.Contract.Id);

    internal void Complete()
    {
        completed = true;
        // All explicit choices are known before any setup hook declares dependencies.
        foreach (var contract in definitions.Keys.ToArray()) Resolve(contract, null);
    }

    private void Resolve(Type contract, Type? fallback)
    {
        var definition = GetDefinition(contract);
        var section = Section(definition);
        var local = definition.Local is not null ||
            (definition.Remote is null && IsLocal(section));
        if (local && definition.Local is null && definition.Configured is null && fallback is not null)
        {
            if (definition.Inferred is not null && definition.Inferred != fallback)
                throw new ArgumentException($"{section.Path}: conflicting dependency implementations " +
                    $"{definition.Inferred.Name} and {fallback.Name}; select one explicitly in the host.");
            definition.Inferred = fallback;
        }
        if (resolving.Contains(contract))
        {
            var path = resolving.Append(contract).Select(type =>
            {
                var descriptor = definitions[type].Registration.Contract;
                return $"{descriptor.Id}/v{descriptor.Version}";
            });
            throw new ArgumentException($"Module dependency cycle: {string.Join(" -> ", path)}.");
        }
        if (definition.Resolved) return;
        if (!local)
        {
            definition.Registration.Remote = definition.Remote ?? ReadRemote(section);
            definition.Resolved = true;
            return;
        }
        var implementation = definition.Local ?? definition.Configured ?? definition.Inferred
            ?? throw new ArgumentException($"{section.Path}: no local implementation was declared.");
        definition.Registration.ImplementationType = implementation;
        services.TryAddScoped(implementation);
        resolving.Add(contract);
        try
        {
            if (typeof(IMicroModule).IsAssignableFrom(implementation))
            {
                var context = new ModuleSetupContext(services, section, Resolve);
                try
                {
                    // Dispatch through the interface, including explicit static implementations.
                    typeof(MicroModuleBuilder).GetMethod(nameof(ConfigureImplementation),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                        .MakeGenericMethod(implementation).Invoke(null, [context]);
                }
                catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
                finally { context.Complete(); }
            }
            definition.Resolved = true;
        }
        finally { resolving.RemoveAt(resolving.Count - 1); }
    }

    private static void ConfigureImplementation<TImplementation>(ModuleSetupContext context)
        where TImplementation : IMicroModule => TImplementation.Configure(context);

    private static bool IsLocal(IConfigurationSection section)
    {
        var mode = section["Mode"];
        if (mode is null || string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(mode, "Remote", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ArgumentException($"{section.Path}:Mode must be Local or Remote.");
    }

    private static RemoteModuleOptions ReadRemote(IConfiguration configuration)
    {
        var path = (configuration as IConfigurationSection)?.Path ?? "Remote";
        var options = new RemoteModuleOptions();
        foreach (var endpoint in configuration.GetSection("Endpoints").GetChildren())
        {
            if (!Uri.TryCreate(endpoint.Value, UriKind.Absolute, out var uri))
                throw new ArgumentException($"{path}:Endpoints must contain absolute HTTP(S) URLs.");
            options.Endpoints.Add(uri);
        }
        options.AttemptTimeout = ReadTimeout(configuration, "AttemptTimeoutSeconds", options.AttemptTimeout, path);
        options.TotalTimeout = ReadTimeout(configuration, "TotalTimeoutSeconds", options.TotalTimeout, path);
        ValidateRemote(options, path);
        return options;
    }

    private static TimeSpan ReadTimeout(IConfiguration configuration, string key, TimeSpan fallback, string path)
    {
        if (configuration[key] is not { } value) return fallback;
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > uint.MaxValue - 1)
            throw new ArgumentException($"{path}:{key} must be positive and within CancellationTokenSource limits.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static void ValidateRemote(RemoteModuleOptions options, string path)
    {
        if (options.Endpoints.Count == 0 || options.Endpoints.Any(endpoint => endpoint is null ||
            !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0))
            throw new ArgumentException($"{path}:Endpoints requires absolute HTTP(S) URLs without query, fragment or credentials.");
        if (options.Endpoints.Distinct().Count() != options.Endpoints.Count)
            throw new ArgumentException($"{path}:Endpoints must be unique.");
        if (options.AttemptTimeout <= TimeSpan.Zero || options.TotalTimeout <= TimeSpan.Zero ||
            options.AttemptTimeout.TotalMilliseconds > uint.MaxValue - 1 ||
            options.TotalTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentException($"{path}: remote timeouts must be positive and within CancellationTokenSource limits.");
    }

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
        => AddMicroModules(services, new ConfigurationBuilder().Build(), configure);

    public static IServiceCollection AddMicroModules(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MicroModuleBuilder> configure)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleRegistry)))
            throw new InvalidOperationException(
                "Call AddMicroModules once per host and compose all registrations inside it.");
        var registry = new ModuleRegistry();
        var builder = new MicroModuleBuilder(services, registry, configuration);
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