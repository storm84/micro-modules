using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MicroModules.AspNetCore;

public static class MicroModuleEndpointExtensions
{
    public static RouteGroupBuilder MapMicroModules(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var group = endpoints.MapGroup("");
        foreach (var registration in registry.Registrations.Values.Where(value => value.ImplementationType is not null))
        foreach (var operation in registration.Contract.Operations)
        {
            // RequestDelegate is deliberate: the only invocable methods come from the registered descriptor.
            var endpoint = group.MapPost(
                    operation.Path,
                    (Delegate)((HttpContext context) => ExecuteAsync(context, registration, operation)))
                .WithName($"{registration.Contract.Id}.v{registration.Contract.Version}.{operation.Id}")
                .WithTags(registration.Contract.Id)
                .WithSummary(operation.Id)
                .Accepts(operation.RequestType, "application/json")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
                .ProducesProblem(StatusCodes.Status500InternalServerError)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
            if (operation.ResponseType is null)
                endpoint.Produces(StatusCodes.Status204NoContent);
            else
                endpoint.Produces(StatusCodes.Status200OK, operation.ResponseType, "application/json");
        }

        return group;
    }

    private static async Task ExecuteAsync(
        HttpContext context,
        ModuleRegistration registration,
        OperationDescriptor operation)
    {
        try
        {
            if (!context.Request.HasJsonContentType())
            {
                await Results.Problem(statusCode: 415, title: "A JSON request body is required.").ExecuteAsync(context);
                return;
            }

            object? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    operation.RequestType,
                    ModuleRuntime.JsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                await Results.Problem(statusCode: 400, title: "Invalid module request.").ExecuteAsync(context);
                return;
            }

            if (request is null)
            {
                await Results.Problem(statusCode: 400, title: "A non-null request body is required.")
                    .ExecuteAsync(context);
                return;
            }

            var runtime = context.RequestServices.GetRequiredService<ModuleRuntime>();
            var response = await runtime.InvokeLocalAsync(
                registration,
                operation,
                context.RequestServices,
                request,
                context.RequestAborted,
                "http-server");
            if (operation.ResponseType is null)
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            else
            {
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    response,
                    operation.ResponseType,
                    ModuleRuntime.JsonOptions,
                    context.RequestAborted);
            }
        }
        catch (ModuleFaultException exception)
        {
            await Results.Problem(
                    statusCode: exception.StatusCode,
                    title: "Module operation rejected.",
                    detail: exception.Message,
                    extensions: new Dictionary<string, object?> { ["moduleFault"] = true, ["code"] = exception.Code })
                .ExecuteAsync(context);
        }
        catch (ModuleUnavailableException)
        {
            await Results.Problem(statusCode: 503, title: "A required module is unavailable.").ExecuteAsync(context);
        }
        catch (ModuleRemoteException)
        {
            // Do not translate non-replayable upstream failures into a retryable 502/503/504.
            await Results.Problem(statusCode: 500, title: "A required module returned an unexpected response.")
                .ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller disconnected; do not attempt to write a replacement response.
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MicroModules.Endpoints")
                .LogError(
                    exception,
                    "Module {Module} operation {Operation} failed",
                    registration.Contract.Id,
                    operation.Id);
            if (context.Response.HasStarted) context.Abort();
            else await Results.Problem(statusCode: 500, title: "Module operation failed.").ExecuteAsync(context);
        }
    }
}