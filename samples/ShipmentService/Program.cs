using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Parcel.Contracts;
using Parcel.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMicroModules(builder.Configuration, modules =>
{
    modules.AddLocal<IShipmentsModule, ShipmentsModule>();
});
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet("/health", async (ShipmentsDatabase database, CancellationToken token) =>
{
    await using var command = database.Source.CreateCommand("SELECT count(*) FROM parcel_shipments");
    await command.ExecuteScalarAsync(token);
    return Results.Ok(new { status = "ready" });
});
app.Run();
