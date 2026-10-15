using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Parcel.Contracts;
using Parcel.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddParcelStorage(builder.Configuration, "AllInOne");
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IBookingModule, BookingModule>();
    modules.AddLocal<IRatesModule, RatesModule>();
    modules.AddLocal<IShipmentsModule, ShipmentsModule>();
    modules.AddLocal<INotificationsModule, NotificationsModule>();
});
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet("/health", async (Npgsql.NpgsqlDataSource source, CancellationToken token) =>
{
    await using var command = source.CreateCommand("SELECT count(*) FROM parcel_shipments");
    await command.ExecuteScalarAsync(token);
    return Results.Ok(new { status = "ready" });
});
app.Run();
