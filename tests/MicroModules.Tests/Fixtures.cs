using System.Net;
using System.Text;
using MicroModules.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicroModules.Tests;

public sealed record ProbeRequest(int Value = 1);

public sealed record ProbeResponse(int Value, Guid Scope);

public sealed class ScopeStamp
{
    public Guid Id { get; } = Guid.NewGuid();
    public int Notifications { get; set; }
}

[ModuleContract("probe")]
public interface IProbeModule
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken cancellationToken);

    [ModuleOperation("unsafe")]
    Task<ProbeResponse> UnsafeAsync(ProbeRequest request, CancellationToken cancellationToken);

    [ModuleOperation("notify")]
    Task NotifyAsync(ProbeRequest request, CancellationToken cancellationToken);

    [ModuleOperation("crash", RetrySafe = true)]
    Task<ProbeResponse> CrashAsync(ProbeRequest request, CancellationToken cancellationToken);
}

public sealed class ProbeModule(ScopeStamp stamp) : IProbeModule
{
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Value == 999) return Task.FromResult<ProbeResponse>(null!);
        if (request.Value < 0) throw new ModuleFaultException("negative", "Value must be non-negative.", 409);
        return Task.FromResult(new ProbeResponse(request.Value, stamp.Id));
    }

    public Task<ProbeResponse> UnsafeAsync(ProbeRequest request, CancellationToken cancellationToken)
        => ReadAsync(request, cancellationToken);

    public Task NotifyAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stamp.Notifications++;
        return Task.CompletedTask;
    }

    public Task<ProbeResponse> CrashAsync(ProbeRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("a-secret-that-must-not-cross-http");
}

internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => send(request, cancellationToken);
}

internal sealed class TestHost : IAsyncDisposable
{
    public WebApplication App { get; }
    private readonly HttpMessageInvoker invoker;

    private TestHost(WebApplication app)
    {
        App = app;
        invoker = new HttpMessageInvoker(app.GetTestServer().CreateHandler());
    }

    public static async Task<TestHost> StartAsync(bool local = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<ScopeStamp>();
        builder.Services.AddMicroModules(modules =>
        {
            if (local) modules.AddLocal<IProbeModule, ProbeModule>();
            else modules.AddRemote<IProbeModule>(options => options.Endpoints.Add(new Uri("http://elsewhere")));
        });
        var app = builder.Build();
        app.MapMicroModules().WithMetadata("module-group-convention");
        await app.StartAsync();
        return new TestHost(app);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        => invoker.SendAsync(message, token);

    public async ValueTask DisposeAsync()
    {
        invoker.Dispose();
        await App.DisposeAsync();
    }
}

internal static class TestServices
{
    public static ServiceProvider Remote(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Action<RemoteModuleOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMicroModules(modules => modules.AddRemote<IProbeModule>(options =>
        {
            options.Endpoints.Add(new Uri("http://primary"));
            options.Endpoints.Add(new Uri("http://fallback"));
            configure?.Invoke(options);
        }));
        services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler(handler));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static HttpResponseMessage Success(int value = 7) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"value\":{value},\"scope\":\"{Guid.Empty}\"}}",
            Encoding.UTF8,
            "application/json")
    };
}

internal sealed class FailingErrorContent(bool stall) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => throw new NotSupportedException();

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
    {
        if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new HttpRequestException("Error body connection lost.");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class InterruptedContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => throw new NotSupportedException();

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        => Task.FromResult<Stream>(new InterruptedStream());

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class InterruptedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => ValueTask.FromException<int>(new IOException("Response connection lost."));
    }
}

[ModuleContract("caller")]
public interface ICallerModule
{
    [ModuleOperation("read", RetrySafe = true)]
    Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token);
}

public sealed class CallerModule(IProbeModule target) : ICallerModule
{
    public Task<ProbeResponse> ReadAsync(ProbeRequest request, CancellationToken token)
        => target.ReadAsync(request, token);
}