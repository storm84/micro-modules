using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Parcel.Contracts;
using Parcel.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMicroModules(builder.Configuration, modules =>
{
    modules.AddLocal<IBookingModule, BookingModule>();
});
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();
