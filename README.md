# MicroModules

A lightweight .NET 10 library for hosting the same module independently or alongside other modules. Application code injects a C# interface; host composition determines whether its calls execute in process or over HTTP.

This repository contains the library and a runnable A/C/BIG proof of concept. Version 0.1.0 supports request/response communication, runtime proxies, and ordered remote failover.

## Run the demonstration

Prerequisites: a .NET 10 SDK, Python 3, and Docker with Compose. Ports 5101–5104 and 55432 must be available. `global.json` pins the solution to SDK 10.0.401 with patch roll-forward.

```sh
python3 scripts/poc.py
```

The script builds the solution, starts Postgres, and launches three separate .NET processes:

| Host | URL | Hosted modules | Calls to C |
|---|---|---|---|
| A | http://127.0.0.1:5101 | A | HTTP, standalone C preferred |
| C | http://127.0.0.1:5102 | C | Local implementation |
| BIG | http://127.0.0.1:5103 | B and C | B → C in process |

At the `poc>` prompt, use `status`, `stop-c`, `start-c`, or `quit`. `stop-c` makes A fall back to C inside BIG; `start-c` restores its preferred endpoint. Ctrl-C and `quit` stop the host processes started by the script. Postgres and its data remain available.

In another terminal:

```sh
# A calls standalone C, or C inside BIG when standalone C is stopped.
curl -s http://127.0.0.1:5101/modules/a/v1/get-counter \
  -H 'Content-Type: application/json' -d '{}'

# B calls C in the BIG process.
curl -s http://127.0.0.1:5103/modules/b/v1/get-counter \
  -H 'Content-Type: application/json' -d '{}'

# Reuse this ID to replay the same operation safely through either host.
curl -s http://127.0.0.1:5101/modules/a/v1/increment-counter \
  -H 'Content-Type: application/json' \
  -d '{"operationId":"df8b1c94-d3bc-4a50-8357-17490366ca5b","amount":1}'
```

Responses include `counter.value` and `counter.servedBy` (`C` or `BIG`). Logs in `artifacts/poc/` identify `local`, `http-client`, and `http-server` execution.

Run the complete automated topology demonstration with:

```sh
python3 scripts/poc.py smoke
```

It checks failover, recovery, shared data, concurrent writes, conflicting operation IDs, simultaneous duplicate requests across both C hosts, and a write whose response is lost after commit. A test-only forwarding proxy on port 5104 creates the lost-response case; the application has no fault-injection endpoints. The smoke test uses fresh operation IDs and checks relative counter values, so it works against retained demo data.

Stop the database without deleting its data:

```sh
docker compose down
```

To deliberately reset the demo, stop the hosts and run `docker compose down --volumes`. This deletes the demo database. Its schema is initialized when Compose creates a new data volume.

## Define a module

Put DTOs and interfaces in a contracts assembly shared by callers and hosts. The contracts assembly references `MicroModules`.

```csharp
using MicroModules;

public sealed record GetCounterRequest;
public sealed record CounterResult(long Value, string ServedBy);

[ModuleContract("c", Version = 1)]
public interface ICModule
{
    [ModuleOperation("get-counter", RetrySafe = true)]
    Task<CounterResult> GetCounterAsync(
        GetCounterRequest request,
        CancellationToken cancellationToken);
}
```

Implement the interface as an ordinary class. Constructor injection works normally. Keep business validation and authorization in the implementation when they must apply to both local and remote callers.

```csharp
public sealed class CModule(ICounterStore store) : ICModule
{
    public async Task<CounterResult> GetCounterAsync(
        GetCounterRequest request, CancellationToken cancellationToken)
        => new(await store.GetAsync(cancellationToken), "my-host");
}
```

`ICounterStore` represents the application's own storage dependency; the library does not define one.

A consuming module simply injects `ICModule` and calls it. It references the contracts, not C's implementation or HTTP details.

### Compose a host

Hosts exposing HTTP reference `MicroModules.AspNetCore`. Remote-only callers need only `MicroModules`.

```csharp
builder.Services.AddScoped<ICounterStore, CounterStore>();
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IBModule, BModule>();
    modules.AddLocal<ICModule, CModule>();
});

var app = builder.Build();
app.MapMicroModules();
app.Run();
```

Call `AddMicroModules` once per host and compose all registrations inside its callback. Contract proxies and local implementations use scoped registrations by default. Host-specific dependencies remain normal DI registrations.

For a remotely hosted dependency:

```csharp
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IAModule, AModule>();
    modules.AddRemote<ICModule>(builder.Configuration.GetSection("MicroModules:C"));
});
```

```json
{
  "MicroModules": {
    "C": {
      "Endpoints": ["http://127.0.0.1:5102", "http://127.0.0.1:5103"],
      "AttemptTimeoutSeconds": 2,
      "TotalTimeoutSeconds": 5
    }
  }
}
```

Programmatic registration is also supported:

```csharp
modules.AddRemote<ICModule>(options =>
{
    options.Endpoints.Add(new Uri("https://c.internal"));
    options.Endpoints.Add(new Uri("https://big.internal"));
});
```

Endpoint URLs may have a base-path prefix. Configure unique absolute HTTP(S) URLs without embedded credentials, query strings, or fragments. Ordered endpoints must expose the same contract version and logical data.

If the same contract has local and remote registrations, local execution always wins regardless of registration order. A local failure is returned to the caller; it never causes remote failover. `MapMicroModules` exposes only local registrations and invokes their actual implementations, avoiding HTTP-to-proxy recursion.

### Contract rules

- Public, non-generic interfaces, without interface inheritance.
- Explicit module and operation IDs consisting of lowercase letters, digits, and hyphens, starting with a letter. Positive integer contract versions.
- One operation per method; no overloads, generic methods, properties, or static methods.
- Exactly one non-null class/record request DTO and a final `CancellationToken`. Use an empty record and `{}` for operations with no inputs.
- Return `Task` or `Task<T>`. `Task<T>` results must be non-null and JSON-compatible; `Task` maps to HTTP 204.
- HTTP endpoints use POST at `/modules/{module}/v{version}/{operation}` with web-default `System.Text.Json` serialization.

Shape and identity errors fail during registration. Keep DTOs immutable and serializable; local invocation passes references directly and does not serialize or deep-copy them. Do not share EF entities, database contexts, or mutable argument state between modules. Different contract versions can coexist using different interfaces; the library performs no version negotiation.

## Failover and write safety

`RetrySafe` defaults to false. An operation marked true may move to the next configured endpoint after connection/read failures, an attempt timeout, or HTTP 502/503/504. Each endpoint is attempted at most once by the library, within the total budget. Every new call starts with the preferred endpoint, so recovery needs no background polling.

Caller cancellation, business faults, other HTTP errors, redirects, and incompatible JSON responses never trigger fallback. HTTP redirects are disabled by default. Call timeouts cover HTTP attempts and response reading; request serialization happens before the network budget starts. There are no background probes or circuit breakers in v1.

Marking an operation `RetrySafe` declares an application guarantee. It does not create an idempotency key or persist anything. A request can commit before its response is lost, so safe writes need shared deduplication or genuinely idempotent semantics.

The POC implements this inside C using Postgres: insert the operation ID, atomically update the counter, and record the result in one transaction. Concurrent duplicates wait for the winning transaction and return its stored value. A reused ID with a different amount produces a 409 fault. `servedBy` describes the deployment returning the result; the stored counter value remains the same on replay. The library has no Postgres dependency.

| Condition | Caller behavior |
|---|---|
| Deliberate `ModuleFaultException` | Same fault code, message, and 4xx status locally and remotely |
| Unexpected local implementation failure | Original exception through the returned Task |
| Unexpected remote failure / incompatible response | `ModuleRemoteException`; unexpected server details are sanitized |
| Exhausted endpoints, timeout, or unsafe transport failure | `ModuleUnavailableException` |
| Caller cancellation | `OperationCanceledException`; no fallback |

## Adaptation and observability

Use the standard named client to configure authentication or additional delegating handlers:

```csharp
builder.Services.AddHttpClient(MicroModuleServiceCollectionExtensions.HttpClientName)
    .AddHttpMessageHandler<MyAuthenticationHandler>();
```

Preserve the infinite `HttpClient.Timeout`; MicroModules supplies attempt and total cancellation budgets. Additional handlers should not introduce hidden retries for writes. The named client applies to all remote module contracts in this host.

`app.MapMicroModules()` returns a route group, so standard endpoint conventions such as `.RequireAuthorization()` apply. Configure ASP.NET authentication/authorization normally. HTTP endpoint authorization protects remote access; authorization needed for local calls also belongs in module/application logic. The unauthenticated, loopback-bound POC is a local demonstration.

The library uses standard `ILogger` and the `MicroModules` ActivitySource, with module ID, operation, version, execution mode, duration, and endpoint information. An OpenTelemetry listener can subscribe using `.AddSource("MicroModules")`; no telemetry exporter is bundled.

Runtime proxies use `DispatchProxy` and cached contract metadata. They avoid networking and serialization for local calls, but retain small proxy/reflection overhead. Native AOT, trimming compatibility, source generation, streaming, gRPC, durable events, service discovery integrations, and cross-module database transactions are outside v1.

## Optional Scalar API reference

Reference `MicroModules.Scalar` in a host to add an interactive Scalar reference and generated OpenAPI document. This optional package brings in `MicroModules.AspNetCore`, `Scalar.AspNetCore`, and Microsoft's OpenAPI generator. `MicroModules` and `MicroModules.AspNetCore` have no dependency on the optional package or its generators.

```csharp
using MicroModules.AspNetCore;
using MicroModules.Scalar;

// After registering your modules:
builder.Services.AddMicroModulesScalar();

var app = builder.Build();
app.MapMicroModules();
if (app.Environment.IsDevelopment())
    app.MapMicroModulesScalar();
app.Run();
```

The default routes are `/scalar` for the interactive reference and `/openapi/v1.json` for the document. Module operations are grouped by module ID and show their request/response DTO schemas; operations returning `Task` document HTTP 204. Remote-only module registrations are not exposed or documented as local APIs. The `v1` document name is independent of module contract versions: one document includes all versions exposed by this host, along with its other HTTP APIs.

Each POC host opts in during Development, as set by the launch script. Browse:

- A: http://127.0.0.1:5101/scalar
- Standalone C: http://127.0.0.1:5102/scalar
- BIG: http://127.0.0.1:5103/scalar

Use the reference to send a JSON request, including `{}` for counter reads. BIG lists both B and C; A lists its A operations and calls C remotely when those operations execute.

Customize the document using standard OpenAPI options, and the UI using standard Scalar options:

```csharp
using Scalar.AspNetCore;

builder.Services.AddMicroModulesScalar(options =>
    options.AddDocumentTransformer((document, context, token) =>
    {
        document.Info.Title = "My application API";
        return Task.CompletedTask;
    }));

// After app.MapMicroModules():
app.MapMicroModulesScalar("/docs", options => options.WithTitle("My application API"));
```

`MapMicroModulesScalar` returns a route group so conventions such as `.RequireAuthorization()` protect both the document and UI. The library maps documentation only when explicitly called; the sample hosts choose the Development condition. Applications already configuring their own OpenAPI documents can use Scalar's standard mapping directly instead of this convenience wrapper.

Module endpoints provide standard ASP.NET request/response metadata even without the Scalar package, so other documentation integrations can also consume it.

## Build, test, and package

```sh
dotnet build MicroModules.sln
dotnet test MicroModules.sln
dotnet pack src/MicroModules/MicroModules.csproj -c Release -o artifacts/packages
dotnet pack src/MicroModules.AspNetCore/MicroModules.AspNetCore.csproj -c Release -o artifacts/packages
dotnet pack src/MicroModules.Scalar/MicroModules.Scalar.csproj -c Release -o artifacts/packages
```

Only the three library projects are packable. The packages are created locally; they are not published automatically. To adopt the library now, use project references or a local NuGet feed containing the generated packages.

Tests exercise in-process invocation with an HTTP handler that fails if called, scoped dependencies, automatic endpoints, business faults, malformed bodies/results, cancellation, time budgets, and retry policy. `scripts/poc.py smoke` supplies the real Postgres and multi-process verification.

Library source lives under `src/`, the modules and hosts under `samples/`, and tests under `tests/`.
