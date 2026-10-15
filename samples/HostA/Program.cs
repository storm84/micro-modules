using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Poc.Contracts;
using Poc.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IAModule, AModule>();
    modules.AddRemote<ICModule>(builder.Configuration.GetSection("MicroModules:C"));
});
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();