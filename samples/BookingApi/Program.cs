using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Parcel.Contracts;
using Parcel.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IBookingModule, BookingModule>();
    modules.AddRemote<IRatesModule>(builder.Configuration.GetSection("MicroModules:Services"));
    modules.AddRemote<IShipmentsModule>(builder.Configuration.GetSection("MicroModules:Services"));
    modules.AddRemote<INotificationsModule>(builder.Configuration.GetSection("MicroModules:Services"));
});
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();
