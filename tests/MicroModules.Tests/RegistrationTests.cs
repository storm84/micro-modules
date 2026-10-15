using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MicroModules.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void Duplicate_local_registration_is_rejected()
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(modules =>
            modules.AddLocal<IProbeModule, ProbeModule>().AddLocal<IProbeModule, ProbeModule>()));

    [Fact]
    public void Duplicate_module_identity_is_rejected()
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(modules =>
        {
            modules.AddRemote<IProbeModule>(options => options.Endpoints.Add(new Uri("http://a")));
            modules.AddRemote<IDuplicateModule>(options => options.Endpoints.Add(new Uri("http://b")));
        }));

    [Theory]
    [InlineData(typeof(IMissingAttribute))]
    [InlineData(typeof(ISynchronous))]
    [InlineData(typeof(IWrongParameters))]
    [InlineData(typeof(IDuplicateOperations))]
    [InlineData(typeof(IMissingOperation))]
    [InlineData(typeof(IOverloads))]
    [InlineData(typeof(IInvalidId))]
    [InlineData(typeof(IInvalidVersion))]
    public void Invalid_contracts_fail_during_registration(Type contract)
    {
        var generic = typeof(MicroModuleBuilder).GetMethods()
            .Single(method => method.Name == "AddRemote" &&
                              method.GetParameters()[0].ParameterType == typeof(Action<RemoteModuleOptions>));
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            new ServiceCollection().AddMicroModules(modules => generic.MakeGenericMethod(contract)
                .Invoke(
                    modules,
                    [(Action<RemoteModuleOptions>)(options => options.Endpoints.Add(new Uri("http://a")))])));
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Theory]
    [InlineData("ftp://server")]
    [InlineData("http://server?query=x")]
    [InlineData("http://server/#fragment")]
    [InlineData("http://user:password@server")]
    public void Invalid_endpoints_are_rejected(string endpoint)
        => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(modules =>
            modules.AddRemote<IProbeModule>(options => options.Endpoints.Add(new Uri(endpoint)))));

    [Fact]
    public void Missing_endpoints_and_invalid_timeouts_are_rejected()
    {
        Assert.Throws<ArgumentException>(()
            => new ServiceCollection().AddMicroModules(modules => modules.AddRemote<IProbeModule>(_ => { })));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMicroModules(modules
            => modules.AddRemote<IProbeModule>(options =>
            {
                options.Endpoints.Add(new Uri("http://server"));
                options.TotalTimeout = TimeSpan.Zero;
            })));
    }

    [Fact]
    public async Task Remote_options_are_snapshotted_when_registered()
    {
        RemoteModuleOptions? captured = null;
        var services = new ServiceCollection();
        services.AddMicroModules(modules => modules.AddRemote<IProbeModule>(options =>
        {
            captured = options;
            options.Endpoints.Add(new Uri("http://primary"));
        }));
        captured!.Endpoints.Clear();
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((request, _) =>
            {
                Assert.Equal("primary", request.RequestUri!.Host);
                return Task.FromResult(TestServices.Success());
            }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default);
    }

    [Fact]
    public void Configuration_registration_works()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["C:Endpoints:0"] = "http://primary",
                    ["C:Endpoints:1"] = "http://fallback",
                    ["C:AttemptTimeoutSeconds"] = "0.1",
                    ["C:TotalTimeoutSeconds"] = "0.2"
                })
            .Build();
        var services = new ServiceCollection();
        services.AddMicroModules(modules => modules.AddRemote<IProbeModule>(configuration.GetSection("C")));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProbeModule>());
    }
}

public interface IMissingAttribute
{
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("sync")]
public interface ISynchronous
{
    [ModuleOperation("read")]
    ProbeResponse Read(ProbeRequest request, CancellationToken token);
}

[ModuleContract("parameters")]
public interface IWrongParameters
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(string request);
}

[ModuleContract("duplicates")]
public interface IDuplicateOperations
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);

    [ModuleOperation("read")]
    Task<ProbeResponse> AnotherAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("missing")]
public interface IMissingOperation
{
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("overloads")]
public interface IOverloads
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);

    [ModuleOperation("read-again")]
    Task<ProbeResponse> ReadAsync(ProbeResponse request, CancellationToken token);
}

[ModuleContract("probe")]
public interface IDuplicateModule
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("bad\n")]
public interface IInvalidId
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

[ModuleContract("bad-version", Version = 0)]
public interface IInvalidVersion
{
    [ModuleOperation("read")]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}