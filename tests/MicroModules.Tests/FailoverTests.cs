using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace MicroModules.Tests;

public sealed class FailoverTests
{
    [Fact]
    public async Task Connection_failure_fails_over_and_next_call_returns_to_recovered_primary()
    {
        var available = false;
        var calls = new List<string>();
        using var provider = TestServices.Remote((request, _) =>
        {
            calls.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "primary" && !available) throw new HttpRequestException("offline");
            return Task.FromResult(TestServices.Success());
        });
        using var scope = provider.CreateScope();
        var module = scope.ServiceProvider.GetRequiredService<IProbeModule>();
        Assert.Equal(7, (await module.ReadAsync(new ProbeRequest(), default)).Value);
        Assert.Equal(new[] { "primary", "fallback" }, calls);
        available = true;
        calls.Clear();
        await module.ReadAsync(new ProbeRequest(), default);
        Assert.Equal(new[] { "primary" }, calls);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Explicit_unavailability_fails_over_for_safe_operations(int status)
    {
        var count = 0;
        using var provider = TestServices.Remote((_, _) => Task.FromResult(
            ++count == 1
                ? new HttpResponseMessage((HttpStatusCode)status)
                : TestServices.Success()));
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task Ordinary_errors_never_fail_over(int status)
    {
        var count = 0;
        using var provider = TestServices.Remote((_, _) =>
        {
            count++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        using var scope = provider.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleRemoteException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default));
        Assert.Equal(status, fault.StatusCode);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("status")]
    [InlineData("timeout")]
    public async Task Unsafe_operations_are_attempted_once(string failure)
    {
        var count = 0;
        using var provider = TestServices.Remote(
            async (_, token) =>
            {
                count++;
                if (failure == "connection") throw new HttpRequestException("offline");
                if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            },
            options => options.AttemptTimeout = TimeSpan.FromMilliseconds(50));
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<ModuleUnavailableException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().UnsafeAsync(new ProbeRequest(), default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Attempt_timeout_allows_safe_fallback()
    {
        var count = 0;
        using var provider = TestServices.Remote(
            async (_, token) =>
            {
                if (++count == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TestServices.Success();
            },
            options => options.AttemptTimeout = TimeSpan.FromMilliseconds(50));
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Total_budget_stops_further_attempts()
    {
        var count = 0;
        using var provider = TestServices.Remote(
            async (_, token) =>
            {
                count++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TestServices.Success();
            },
            options =>
            {
                options.AttemptTimeout = TimeSpan.FromSeconds(2);
                options.TotalTimeout = TimeSpan.FromMilliseconds(50);
            });
        using var scope = provider.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleUnavailableException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default));
        Assert.Contains("total call timeout", fault.Message);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Caller_cancellation_never_fails_over()
    {
        var count = 0;
        using var provider = TestServices.Remote(async (_, token) =>
        {
            count++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestServices.Success();
        });
        using var scope = provider.CreateScope();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), caller.Token));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("null", 200)]
    [InlineData("broken", 200)]
    [InlineData("", 204)]
    public async Task Incompatible_success_responses_are_not_retried(string body, int status)
    {
        var count = 0;
        using var provider = TestServices.Remote((_, _) =>
        {
            count++;
            return Task.FromResult(
                new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        });
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<ModuleRemoteException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_reading_a_business_error_body_does_not_trigger_fallback(bool stall)
    {
        var count = 0;
        using var provider = TestServices.Remote(
            (_, _) =>
            {
                count++;
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new FailingErrorContent(stall) });
            },
            options => options.AttemptTimeout = TimeSpan.FromMilliseconds(50));
        using var scope = provider.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleRemoteException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default));
        Assert.Equal(409, fault.StatusCode);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Interrupted_success_response_can_fail_over_for_safe_operations()
    {
        var count = 0;
        using var provider = TestServices.Remote((_, _) => Task.FromResult(
            ++count == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new InterruptedContent() }
                : TestServices.Success()));
        using var scope = provider.CreateScope();
        Assert.Equal(
            7,
            (await scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default))
            .Value);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task All_unavailable_endpoints_produce_a_clear_failure()
    {
        var count = 0;
        using var provider = TestServices.Remote((_, _) =>
        {
            count++;
            throw new HttpRequestException("offline");
        });
        using var scope = provider.CreateScope();
        var fault = await Assert.ThrowsAsync<ModuleUnavailableException>(()
            => scope.ServiceProvider.GetRequiredService<IProbeModule>().ReadAsync(new ProbeRequest(), default));
        Assert.Contains("all configured endpoints", fault.Message);
        Assert.Equal(2, count);
    }
}