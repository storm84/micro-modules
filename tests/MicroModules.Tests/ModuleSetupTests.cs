using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MicroModules.Tests;

public sealed class ModuleSetupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(
            item => "MicroModules:" + item.Key, item => item.Value)).Build();

    [Fact]
    public async Task One_root_sets_up_transitive_shared_dependencies_once_without_http()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Value", "37")), modules =>
            modules.AddModule<ISetupRoot, SetupRoot>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((_, _) =>
                throw new Xunit.Sdk.XunitException("Local dependencies must not send HTTP.")));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var root = await scope.ServiceProvider.GetRequiredService<ISetupRoot>().ReadAsync(new(), default);
        var leaf = await scope.ServiceProvider.GetRequiredService<ISetupLeaf>().ReadAsync(new(), default);
        Assert.Equal(37, root.Value);
        Assert.Equal(root.Scope, leaf.Scope);
        using var next = provider.CreateScope();
        Assert.NotEqual(root.Scope, (await next.ServiceProvider.GetRequiredService<ISetupRoot>().ReadAsync(new(), default)).Scope);
    }

    [Fact]
    public void Legacy_AddLocal_runs_optional_setup_and_owned_resources_are_disposed()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(modules => modules.AddLocal<ISetupRoot, SetupRoot>());
        var provider = services.BuildServiceProvider();
        var resource = provider.GetRequiredService<SetupResource>();
        Assert.False(resource.Disposed);
        provider.Dispose();
        Assert.True(resource.Disposed);
    }

    [Fact]
    public async Task Configured_remote_dependency_skips_local_settings_and_resources()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Mode", "Remote"),
            ("setup-leaf:Endpoints:0", "http://leaf"), ("setup-leaf:RequireSetting", "true")),
            modules => modules.AddLocal<ISetupRoot, SetupRoot>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((request, _) =>
            {
                Assert.Equal("leaf", request.RequestUri!.Host);
                return Task.FromResult(TestServices.Success(81));
            }));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.Equal(81, (await scope.ServiceProvider.GetRequiredService<ISetupRoot>().ReadAsync(new(), default)).Value);
    }

    [Fact]
    public void Remote_root_skips_its_entire_dependency_graph()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-root:Mode", "Remote"), ("setup-root:Endpoints:0", "http://root"),
            ("setup-leaf:Mode", "invalid")), modules => modules.AddModule<ISetupRoot, SetupRoot>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISetupLeaf));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISetupBranch));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupRoot));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_remote_overrides_inferred_local_regardless_of_order(bool first)
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Mode", "Local"), ("setup-leaf:RequireSetting", "true")), modules =>
        {
            if (first) modules.AddRemote<ISetupLeaf>(options => options.Endpoints.Add(new Uri("http://explicit")));
            modules.AddModule<ISetupRoot, SetupRoot>();
            if (!first) modules.AddRemote<ISetupLeaf>(options => options.Endpoints.Add(new Uri("http://explicit")));
        });
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((request, _) =>
            {
                Assert.Equal("explicit", request.RequestUri!.Host);
                return Task.FromResult(TestServices.Success(83));
            }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal(83, (await scope.ServiceProvider.GetRequiredService<ISetupRoot>().ReadAsync(new(), default)).Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_local_implementation_overrides_configuration_and_dependency_default(bool first)
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Mode", "Remote")), modules =>
        {
            if (first) modules.AddLocal<ISetupLeaf, AlternateLeaf>();
            modules.AddModule<ISetupRoot, SetupRoot>();
            if (!first) modules.AddLocal<ISetupLeaf, AlternateLeaf>();
        });
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.Equal(99, (await scope.ServiceProvider.GetRequiredService<ISetupRoot>().ReadAsync(new(), default)).Value);
    }

    [Fact]
    public void Parameterless_AddRemote_uses_the_contract_section_without_local_implementation()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Endpoints:0", "http://leaf")), modules => modules.AddRemote<ISetupLeaf>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISetupLeaf>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupLeaf));
    }

    [Fact]
    public void Cycles_report_the_full_module_path()
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(
            modules => modules.AddLocal<ISetupRoot, CyclicRoot>()));
        Assert.Contains("setup-root/v1 -> setup-branch/v1 -> setup-root/v1", error.Message);
    }

    [Fact]
    public void Conflicting_inferred_implementations_require_a_host_choice()
    {
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(modules =>
        {
            modules.AddModule<ISetupRoot, SetupRoot>();
            modules.AddModule<ISetupOther, OtherRoot>();
        }));
        Assert.Contains("MicroModules:setup-leaf", error.Message);
        Assert.Contains("conflicting", error.Message);
    }

    [Fact]
    public void Configurable_explicit_implementation_resolves_conflicting_defaults()
    {
        new ServiceCollection().AddMicroModules(modules =>
        {
            modules.AddModule<ISetupRoot, SetupRoot>();
            modules.AddModule<ISetupOther, OtherRoot>();
            modules.AddModule<ISetupLeaf, AlternateLeaf>();
        });
    }

    [Fact]
    public void Remote_choice_resolves_conflicting_local_defaults_without_running_them()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(Config(("setup-leaf:Mode", "Remote"), ("setup-leaf:Endpoints:0", "http://leaf")), modules =>
        {
            modules.AddModule<ISetupRoot, SetupRoot>();
            modules.AddModule<ISetupOther, OtherRoot>();
        });
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(SetupResource));
    }

    [Theory]
    [InlineData("Mode", "invalid")]
    [InlineData("Endpoints:0", "http://user:secret@host")]
    [InlineData("AttemptTimeoutSeconds", "Infinity")]
    [InlineData("TotalTimeoutSeconds", "invalid")]
    public void Invalid_selected_configuration_names_the_path_without_echoing_values(string key, string value)
    {
        var mode = key == "Mode" ? value : "Remote";
        var configuration = key == "Mode"
            ? Config(("setup-leaf:Mode", mode))
            : Config(("setup-leaf:Mode", mode), ("setup-leaf:" + key, value));
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(
            configuration, modules => modules.AddModule<ISetupLeaf, SetupLeaf>()));
        Assert.Contains("MicroModules:setup-leaf", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void Missing_remote_endpoints_and_missing_local_settings_fail_during_registration()
    {
        var remote = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(
            Config(("setup-leaf:Mode", "Remote")), modules => modules.AddModule<ISetupLeaf, SetupLeaf>()));
        Assert.Contains("MicroModules:setup-leaf:Endpoints", remote.Message);
        var local = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(
            Config(("setup-leaf:RequireSetting", "true")), modules => modules.AddLocal<ISetupLeaf, SetupLeaf>()));
        Assert.Contains("MicroModules:setup-leaf:Value", local.Message);
    }

    [Fact]
    public void Static_explicit_interface_setup_is_supported_and_context_is_closed_afterwards()
    {
        var services = new ServiceCollection();
        services.AddMicroModules(modules => modules.AddLocal<ISetupLeaf, ExplicitSetupLeaf>());
        using var provider = services.BuildServiceProvider();
        var captured = provider.GetRequiredService<CapturedSetup>();
        Assert.Throws<InvalidOperationException>(() => captured.Context.AddDependency<ISetupBranch, SetupBranch>());
    }
}

[ModuleContract("setup-root")]
public interface ISetupRoot
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("setup-branch")]
public interface ISetupBranch
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("setup-leaf")]
public interface ISetupLeaf
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("setup-other")]
public interface ISetupOther
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

public sealed class SetupRoot(ISetupBranch branch, ISetupLeaf leaf) : ISetupRoot, IMicroModule
{
    public static void Configure(ModuleSetupContext context)
    {
        context.AddDependency<ISetupBranch, SetupBranch>();
        context.AddDependency<ISetupLeaf, SetupLeaf>();
    }
    public async Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
    {
        _ = leaf;
        return await branch.ReadAsync(request, token);
    }
}

public sealed class SetupBranch(ISetupLeaf leaf) : ISetupBranch, IMicroModule
{
    public static void Configure(ModuleSetupContext context) => context.AddDependency<ISetupLeaf, SetupLeaf>();
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token) => leaf.ReadAsync(request, token);
}

public sealed class SetupResource(int value) : IDisposable
{
    public int Value { get; } = value;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public sealed class SetupLeaf(SetupResource resource, ScopeStamp stamp) : ISetupLeaf, IMicroModule
{
    public static void Configure(ModuleSetupContext context)
    {
        if (context.Configuration["RequireSetting"] == "true" && context.Configuration["Value"] is null)
            throw new ArgumentException(context.Configuration.Path + ":Value is required.");
        var value = int.Parse(context.Configuration["Value"] ?? "7");
        context.Services.AddSingleton(_ => new SetupResource(value));
        context.Services.AddScoped<ScopeStamp>();
    }
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
        => Task.FromResult(new ProbeResponse(resource.Value, stamp.Id));
}

public sealed class AlternateLeaf : ISetupLeaf
{
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
        => Task.FromResult(new ProbeResponse(99, Guid.Empty));
}

public sealed class OtherRoot : ISetupOther, IMicroModule
{
    public static void Configure(ModuleSetupContext context) => context.AddDependency<ISetupLeaf, AlternateLeaf>();
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
        => Task.FromResult(new ProbeResponse(1, Guid.Empty));
}

public sealed class CyclicRoot : ISetupRoot, IMicroModule
{
    public static void Configure(ModuleSetupContext context) => context.AddDependency<ISetupBranch, CyclicBranch>();
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token) => throw new NotSupportedException();
}

public sealed class CyclicBranch : ISetupBranch, IMicroModule
{
    public static void Configure(ModuleSetupContext context) => context.AddDependency<ISetupRoot, CyclicRoot>();
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token) => throw new NotSupportedException();
}

public sealed record CapturedSetup(ModuleSetupContext Context);

public sealed class ExplicitSetupLeaf : ISetupLeaf, IMicroModule
{
    static void IMicroModule.Configure(ModuleSetupContext context) => context.Services.AddSingleton(new CapturedSetup(context));
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
        => Task.FromResult(new ProbeResponse(1, Guid.Empty));
}
