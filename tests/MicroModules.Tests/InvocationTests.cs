using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MicroModules.Tests;

public sealed class InvocationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_always_wins_and_keeps_scoped_dependencies(bool remoteFirst)
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeStamp>();
        services.AddMicroModules(modules =>
        {
            if (remoteFirst)
                modules.AddRemote<IProbeModule>(options => options.Endpoints.Add(new Uri("http://unreachable")));
            modules.AddLocal<IProbeModule, ProbeModule>();
            modules.AddLocal<ICallerModule, CallerModule>();
            if (!remoteFirst)
                modules.AddRemote<IProbeModule>(options => options.Endpoints.Add(new Uri("http://unreachable")));
        });
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(()
                => new DelegateHandler((_, _)
                    => throw new Xunit.Sdk.XunitException("Local calls must never send HTTP.")));
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IProbeModule>();
        var second = secondScope.ServiceProvider.GetRequiredService<IProbeModule>();
        // A hosted caller invokes another hosted module while outbound HTTP is forbidden.
        var caller = firstScope.ServiceProvider.GetRequiredService<ICallerModule>();
        var result = await caller.ReadAsync(new ProbeRequest(17), default);
        Assert.Equal(17, result.Value);
        Assert.Equal(firstScope.ServiceProvider.GetRequiredService<ScopeStamp>().Id, result.Scope);
        Assert.Equal(result.Scope, (await first.ReadAsync(new ProbeRequest(), default)).Scope);
        Assert.NotEqual(result.Scope, (await second.ReadAsync(new ProbeRequest(), default)).Scope);
        await first.NotifyAsync(new ProbeRequest(), default);
        Assert.Equal(1, firstScope.ServiceProvider.GetRequiredService<ScopeStamp>().Notifications);
        var fault =
            await Assert.ThrowsAsync<ModuleFaultException>(() => caller.ReadAsync(new ProbeRequest(-1), default));
        Assert.Equal("negative", fault.Code);
    }

    [Fact]
    public async Task Local_and_http_calls_share_business_results_and_faults()
    {
        await using var host = await TestHost.StartAsync();
        using var remote = TestServices.Remote(host.SendAsync);
        using var scope = remote.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<IProbeModule>();
        Assert.Equal(42, (await module.ReadAsync(new ProbeRequest(42), default)).Value);
        var fault =
            await Assert.ThrowsAsync<ModuleFaultException>(() => module.ReadAsync(new ProbeRequest(-1), default));
        Assert.Equal("negative", fault.Code);
        Assert.Equal(409, fault.StatusCode);
        Assert.Equal("Value must be non-negative.", fault.Message);
        await module.NotifyAsync(new ProbeRequest(), default);
    }

    [Fact]
    public async Task Unexpected_http_faults_are_sanitized_and_not_retried()
    {
        await using var host = await TestHost.StartAsync();
        var count = 0;
        using var remote = TestServices.Remote((request, token) =>
        {
            count++;
            return host.SendAsync(request, token);
        });
        using var scope = remote.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleRemoteException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().CrashAsync(new ProbeRequest(), default));
        Assert.Equal(500, fault.StatusCode);
        Assert.DoesNotContain("secret", fault.ToString());
        Assert.Equal(1, count);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/modules/probe/v1/crash")
            { Content = JsonContent.Create(new ProbeRequest()) };
        using var response = await host.SendAsync(request, default);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("null", "application/json", 400)]
    [InlineData("{", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task Invalid_http_bodies_are_rejected(
        string body,
        string contentType,
        int status)
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/modules/probe/v1/read")
            { Content = new StringContent(body, System.Text.Encoding.UTF8, contentType) };
        using var response = await host.SendAsync(request, default);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Only_local_registered_contracts_are_exposed_and_group_conventions_apply()
    {
        await using var remoteOnly = await TestHost.StartAsync(local: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/modules/probe/v1/read")
            { Content = JsonContent.Create(new ProbeRequest()) };
        using var response = await remoteOnly.SendAsync(request, default);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var local = await TestHost.StartAsync();
        var endpoints = ((IEndpointRouteBuilder)local.App).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Equal(4, endpoints.Length);
        Assert.All(endpoints, endpoint => Assert.Contains("module-group-convention", endpoint.Metadata));
    }

    [Fact]
    public async Task Null_response_results_are_rejected_locally_and_over_http()
    {
        await using var host = await TestHost.StartAsync();
        using var localScope = host.App.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(()
            => localScope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(999), default));
        using var remote = TestServices.Remote(host.SendAsync);
        using var remoteScope = remote.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleRemoteException>(()
            => remoteScope.ServiceProvider.GetRequiredService<IProbeModule>()
                .ReadAsync(new ProbeRequest(999), default));
        Assert.Equal(500, fault.StatusCode);
    }

    [Fact]
    public async Task Local_caller_cancellation_prevents_execution()
    {
        await using var host = await TestHost.StartAsync();
        using var scope = host.App.Services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>()
                .NotifyAsync(new ProbeRequest(), cancellation.Token));
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<ScopeStamp>().Notifications);
    }
}