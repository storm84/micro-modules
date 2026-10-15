using MicroModules;
using Parcel.Contracts;
using Parcel.Modules;

var builder = WebApplication.CreateBuilder(args);
// Execution mode and resources are selected independently for each module in configuration.
builder.Services.AddMicroModules(builder.Configuration, modules =>
{
    modules.AddModule<IShipmentsModule, ShipmentsModule>();
    modules.AddModule<INotificationsModule, NotificationsModule>();
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ModuleFaultException error)
    {
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new { code = error.Code, message = error.Message }, context.RequestAborted);
    }
    catch (ModuleUnavailableException)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { code = "services-unavailable", message = "Shipment services are unavailable. Try again shortly." }, context.RequestAborted);
    }
    catch (ModuleRemoteException)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { code = "service-error", message = "A shipment service could not complete the request. Try again shortly." }, context.RequestAborted);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/shipments", (int? offset, int? limit, IShipmentsModule shipments, CancellationToken token)
    => shipments.ListAsync(new(offset ?? 0, limit ?? 20), token));
app.MapGet("/api/shipments/{id:guid}", async (Guid id, IShipmentsModule shipments,
    INotificationsModule notifications, CancellationToken token) =>
{
    var result = await shipments.GetAsync(new(id), token);
    var inbox = await notifications.GetInboxAsync(new(id), token);
    return new BookingDetails(result.Shipment, result.ServedBy, inbox.Messages);
});
app.MapPost("/api/shipments/{id:guid}/notifications", async (Guid id, CustomNotificationInput input,
    IShipmentsModule shipments, CancellationToken token) =>
{
    var queued = await shipments.SendNotificationAsync(new(input.OperationId, id, input.Subject, input.Body), token);
    return Results.Json(queued, statusCode: 202);
});
// Module setup owns resources and workers. This web host exposes only its browser adapters.
app.Run();

public sealed record CustomNotificationInput(Guid OperationId, string Subject, string Body);
