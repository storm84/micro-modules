using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicroModules;

internal sealed class ModuleRuntime(IHttpClientFactory clients, ILogger<ModuleRuntime> logger)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static readonly ActivitySource Activities = new("MicroModules");

    internal async Task<object?> InvokeAsync(
        ModuleRegistration registration,
        OperationDescriptor operation,
        IServiceProvider provider,
        object? request,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        return registration.ImplementationType is not null
            ? await InvokeLocalAsync(
                    registration,
                    operation,
                    provider,
                    request,
                    token,
                    "local")
                .ConfigureAwait(false)
            : await InvokeRemoteAsync(
                    registration,
                    operation,
                    request,
                    token)
                .ConfigureAwait(false);
    }

    internal async Task<object?> InvokeLocalAsync(
        ModuleRegistration registration,
        OperationDescriptor operation,
        IServiceProvider provider,
        object request,
        CancellationToken token,
        string mode)
    {
        token.ThrowIfCancellationRequested();
        using var activity = StartActivity(registration, operation, mode);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var implementation = provider.GetRequiredService(registration.ImplementationType!);
            var response = await operation.InvokeAsync(implementation, request, token).ConfigureAwait(false);
            if (operation.ResponseType is not null && response is null)
                throw new InvalidOperationException($"{operation.Method.Name} must return a non-null response DTO.");
            logger.LogInformation(
                "Module {Module} operation {Operation} mode {Mode} completed in {DurationMs} ms",
                registration.Contract.Id,
                operation.Id,
                mode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            logger.LogWarning(
                exception,
                "Module {Module} operation {Operation} mode {Mode} failed after {DurationMs} ms",
                registration.Contract.Id,
                operation.Id,
                mode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private async Task<object?> InvokeRemoteAsync(
        ModuleRegistration registration,
        OperationDescriptor operation,
        object request,
        CancellationToken callerToken)
    {
        var options = registration.Remote!;
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, operation.RequestType, JsonOptions);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        total.CancelAfter(options.TotalTimeout);
        using var client = clients.CreateClient(MicroModuleServiceCollectionExtensions.HttpClientName);
        Exception? lastFailure = null;
        foreach (var endpoint in options.Endpoints)
        {
            callerToken.ThrowIfCancellationRequested();
            if (total.IsCancellationRequested)
                throw new ModuleUnavailableException(
                    $"Module {registration.Contract.Id}: total call timeout elapsed.",
                    lastFailure);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            attempt.CancelAfter(options.AttemptTimeout);
            var uri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + operation.Path);
            using var activity = StartActivity(registration, operation, "http-client");
            activity?.SetTag("server.address", endpoint.Host);
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, uri);
                message.Content = new ByteArrayContent(payload);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    object? result = null;
                    if (operation.ResponseType is not null)
                    {
                        if (response.StatusCode == HttpStatusCode.NoContent)
                            throw new ModuleRemoteException(
                                "Expected a JSON response, received no content.",
                                endpoint,
                                (int)response.StatusCode);
                        await using var stream =
                            await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                        try
                        {
                            result = await JsonSerializer.DeserializeAsync(
                                    stream,
                                    operation.ResponseType,
                                    JsonOptions,
                                    attempt.Token)
                                .ConfigureAwait(false);
                            if (result is null) throw new JsonException("Module response was null.");
                        }
                        catch (JsonException exception)
                        {
                            throw new ModuleRemoteException(
                                "Module returned an incompatible JSON response.",
                                endpoint,
                                (int)response.StatusCode,
                                exception);
                        }
                    }

                    logger.LogInformation(
                        "Module {Module} operation {Operation} mode http-client endpoint {Endpoint} completed in {DurationMs} ms",
                        registration.Contract.Id,
                        operation.Id,
                        endpoint,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return result;
                }

                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout)
                    throw new EndpointUnavailableException($"Endpoint {endpoint} returned {(int)response.StatusCode}.");
                await ThrowResponseFaultAsync(
                        response,
                        endpoint,
                        attempt.Token,
                        callerToken)
                    .ConfigureAwait(false);
                throw new UnreachableException();
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "caller-cancelled");
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                                                  or EndpointUnavailableException or OperationCanceledException)
            {
                lastFailure = exception;
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                logger.LogWarning(
                    exception,
                    "Module {Module} operation {Operation} mode http-client endpoint {Endpoint} failed after {DurationMs} ms",
                    registration.Contract.Id,
                    operation.Id,
                    endpoint,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (total.IsCancellationRequested)
                    throw new ModuleUnavailableException(
                        $"Module {registration.Contract.Id}: total call timeout elapsed.",
                        exception);
                if (!operation.RetrySafe)
                    throw new ModuleUnavailableException(
                        $"Module {registration.Contract.Id}: call failed; operation is not declared RetrySafe and was not retried.",
                        exception);
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                logger.LogWarning(
                    exception,
                    "Module {Module} operation {Operation} mode http-client endpoint {Endpoint} rejected after {DurationMs} ms",
                    registration.Contract.Id,
                    operation.Id,
                    endpoint,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw;
            }
        }

        throw new ModuleUnavailableException(
            $"Module {registration.Contract.Id}: all configured endpoints failed.",
            lastFailure);
    }

    private static async Task ThrowResponseFaultAsync(
        HttpResponseMessage response,
        Uri endpoint,
        CancellationToken token,
        CancellationToken callerToken)
    {
        if ((int)response.StatusCode is >= 400 and <= 499)
        {
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var document =
                    await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("moduleFault", out var fault) &&
                    fault.ValueKind == JsonValueKind.True &&
                    root.TryGetProperty("code", out var code) &&
                    code.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(code.GetString()) &&
                    root.TryGetProperty("detail", out var detail) &&
                    detail.ValueKind == JsonValueKind.String)
                    throw new ModuleFaultException(code.GetString()!, detail.GetString()!, (int)response.StatusCode);
            }
            catch (JsonException)
            {
                /* An unrecognized error body is not a replayable transport failure. */
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException ||
                                              exception is OperationCanceledException &&
                                              !callerToken.IsCancellationRequested)
            {
                // We already received a non-retryable status. Failure reading its body cannot make it retryable.
                throw new ModuleRemoteException(
                    $"Module endpoint returned HTTP {(int)response.StatusCode}; its error body could not be read.",
                    endpoint,
                    (int)response.StatusCode,
                    exception);
            }
        }

        throw new ModuleRemoteException(
            $"Module endpoint returned HTTP {(int)response.StatusCode}.",
            endpoint,
            (int)response.StatusCode);
    }

    private static Activity? StartActivity(
        ModuleRegistration registration,
        OperationDescriptor operation,
        string mode)
    {
        var activity = Activities.StartActivity(
            $"{registration.Contract.Id}.{operation.Id}",
            mode == "http-client" ? ActivityKind.Client : ActivityKind.Internal);
        activity?.SetTag("module.id", registration.Contract.Id);
        activity?.SetTag("module.version", registration.Contract.Version);
        activity?.SetTag("module.operation", operation.Id);
        activity?.SetTag("module.mode", mode);
        return activity;
    }

    private sealed class EndpointUnavailableException(string message) : Exception(message);
}