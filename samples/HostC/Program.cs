using MicroModules;
using MicroModules.AspNetCore;
using MicroModules.Scalar;
using Poc.Contracts;
using Poc.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCounterStorage(builder.Configuration, "C");
builder.Services.AddMicroModules(modules => modules.AddLocal<ICModule, CModule>());
builder.Services.AddMicroModulesScalar();
var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.MapGet(
    "/health",
    async (CounterStore store, CancellationToken token) =>
    {
        await store.GetAsync(token);
        return Results.Ok(new { status = "ready" });
    });
app.Run();