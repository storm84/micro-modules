# MicroModules

A lightweight .NET 10 library for hosting the same module independently or alongside other modules. Application code injects a C# interface; host composition determines whether its calls execute in process or over HTTP.

This repository contains the library and a runnable parcel-booking sample. Version 0.1.0 supports request/response communication, runtime proxies, and ordered remote failover.

## Run the parcel-booking sample

A fictional shop books a domestic Swedish parcel, follows its tracking history, and receives a booking confirmation and a delivery notification. The same module implementations run in separate processes or together in one host.

Prerequisites: a .NET 10 SDK, Python 3, and Docker with Compose. Ports 5101–5105 and 55432 must be available. `global.json` pins SDK 10.0.401 with patch roll-forward.

```sh
python3 scripts/poc.py
```

The launcher builds the solution, starts Postgres, applies the sample schema, and launches four hosts:

| Host | URL | Local modules | Calls |
|---|---|---|---|
| BookingApi | http://127.0.0.1:5101 | Booking | Rates, Shipments, and Notifications over HTTP; ShipmentService preferred, AllInOne fallback |
| ShipmentService | http://127.0.0.1:5102 | Rates, Shipments, Notifications | Shipments → Rates and outbox worker → Notifications in process |
| AllInOne | http://127.0.0.1:5103 | All four modules | All module calls in process |
| WebApp | http://127.0.0.1:5105 | None | Shipments and Notifications over HTTP; ShipmentService preferred, AllInOne fallback |

At the `parcel>` prompt:

- `demo` books a fresh parcel, waits for confirmation, simulates collection/transit/delivery, and prints the history and both notifications.
- `stop-shipments` makes BookingApi fall back to AllInOne.
- `start-shipments` restores its preferred service.
- `status` shows running host processes; `quit` or Ctrl-C stops them.

Postgres and its data remain available. AllInOne also works when both other hosts are stopped. The two service hosts use the same logical database, and either can process pending notifications.

### Open the web app

Browse [Parcel desk](http://127.0.0.1:5105) to view shipments, tracking history, and the demo notification inbox. Select a shipment, enter a subject and message, and send a custom notification to its stored customer email. Delivery is simulated through the same durable outbox as automatic notifications. The page waits for the new message to appear in the inbox.

Shipments are listed newest first, with Newer/Older paging and a Refresh button. If the database is empty, enter `demo` at the launcher's prompt to create a shipment. Use `stop-shipments` to see the web app continue through AllInOne.

WebApp references only `MicroModules` and `Parcel.Contracts`. It registers remote Shipments and Notifications contracts, serves static HTML/CSS/JavaScript, and maps thin `/api` adapters for the browser. It has no local modules, database connection, outbox worker, or module endpoints. The other hosts own all business validation and persistence.

### Walk through the APIs

Open [BookingApi Scalar](http://127.0.0.1:5101/scalar) or use these requests. All operations use POST with JSON bodies.

Get a quote:

```sh
curl -s http://127.0.0.1:5101/modules/booking/v1/quote \
  -H 'Content-Type: application/json' \
  -d '{"weightGrams":1500,"service":"standard","destinationCountry":"SE"}'
```

The fictional `standard` service costs 49 SEK plus 10 SEK per started kilogram; `express` costs 79 SEK plus the same weight charge. Parcels must weigh 1–20000 grams. Quotes and bookings support Swedish addresses with five-digit postal codes only. Prices are deterministic and have no external dependencies.

Book a parcel:

```sh
curl -s http://127.0.0.1:5101/modules/booking/v1/book \
  -H 'Content-Type: application/json' \
  -d '{
    "operationId":"df8b1c94-d3bc-4a50-8357-17490366ca5b",
    "sender":{"name":"Demo Shop","street":"Shop Street 1","postalCode":"11122","city":"Stockholm","country":"SE"},
    "recipient":{"name":"Alex Example","street":"Example Street 2","postalCode":"41103","city":"Gothenburg","country":"SE"},
    "customerEmail":"alex@example.test",
    "weightGrams":1500,
    "service":"standard"
  }'
```

The response contains `shipment.id`, a synthetic `DEMO-` tracking number, the accepted quote, initial `Booked` event, and `servedBy` (`ShipmentService` or `AllInOne`). The booking implementation obtains the price from Rates; callers do not submit an authoritative price.

Reuse the operation ID and identical payload through either host to retrieve the original booking. Use a fresh operation ID for a new parcel; changing a committed operation's request returns a 409 `operation-id-conflict`.

Copy the shipment ID into these requests:

```sh
curl -s http://127.0.0.1:5101/modules/booking/v1/get-booking \
  -H 'Content-Type: application/json' -d '{"shipmentId":"SHIPMENT_ID"}'

curl -s http://127.0.0.1:5101/modules/booking/v1/simulate-tracking \
  -H 'Content-Type: application/json' \
  -d '{"operationId":"d3a9b6dd-7256-4ee6-8919-7bff4a9cb69d","shipmentId":"SHIPMENT_ID","status":"Collected"}'
```

Advance through `Collected`, `InTransit`, then `Delivered`, using a fresh operation ID for each event. Invalid transitions return 409. Tracking history is append-only, with UTC timestamps. Duplicate tracking operations replay their original result even after later transitions.

`get-booking` combines current shipment details with delivered notifications. Notifications arrive asynchronously; poll again if the inbox is empty immediately after booking or delivery. They contain the recipient email, subject, and body, but no actual email is sent.

### List shipments and send custom notifications

These operations belong to Shipments and are available on ShipmentService and AllInOne:

```sh
curl -s http://127.0.0.1:5102/modules/shipments/v1/list \
  -H 'Content-Type: application/json' -d '{"offset":0,"limit":20}'

curl -s http://127.0.0.1:5102/modules/shipments/v1/send-notification \
  -H 'Content-Type: application/json' \
  -d '{"operationId":"7a026c9e-984f-41c3-ad12-186ae88c5208","shipmentId":"SHIPMENT_ID","subject":"A parcel update","body":"Thank you for your order."}'
```

Listing returns shipment summaries, `hasMore`, and `servedBy`. Offset must be non-negative and limit must be 1–100. Custom notifications require a subject of up to 200 characters and a body of up to 4000 characters; the shipment determines the recipient email. The response is a durable queue receipt. Replaying the same operation ID and payload returns the same receipt; changing its payload returns 409. A missing shipment returns 404 without queueing a message.

### Durable notification delivery

Shipments saves each booking/tracking change, its operation result, and any notification outbox entry in one Postgres transaction. Booking and delivery create automatic notifications; custom notifications use the same transaction and outbox path. A small worker invokes the Notifications module, which stores messages in the demo inbox using a unique notification ID.

Both service hosts run workers. Row locking with `SKIP LOCKED` prevents them processing the same pending row simultaneously. Failed delivery is retried after two seconds, with attempts and the latest error recorded in the outbox. Delivery and acknowledgement commit separately: a crash between them causes a safe replay rather than a second inbox message. Booking succeeds once its transaction commits, independently of notification delivery.

The worker is application code in the sample. MicroModules does not provide durable events or outbox processing. `Outbox:Enabled=false` disables a host's worker for restart demonstrations and testing.

### Automated verification and retained data

```sh
python3 scripts/poc.py smoke
```

This checks quotes, validation, the full parcel lifecycle, local and remote execution, shared data, concurrent writes, operation conflicts, duplicate requests, failover, recovery, pending notifications across restarts, and lost responses after booking and custom-notification commits. It also checks the remote-only web APIs, shipment paging, notification validation and replay, and service-unavailable errors. A test-only forwarding proxy on port 5104 creates the lost-response case; the application has no fault-injection endpoints.

The smoke run also executes parcel database tests in temporary isolated schemas, covering failed delivery, delivery before acknowledgement failure, competing dispatchers, and conflicting tracking transitions. It uses fresh IDs and retained data safely. Logs are written under `artifacts/poc/`.

The launcher applies the idempotent schema on every run, including against an existing counter-demo volume. Old counter tables remain unused; no data reset is required.

```sh
# Stop the database and retain its data.
docker compose down
```

To deliberately reset the demo, stop the hosts and run `docker compose down --volumes`. This deletes the demo database.

## Define a module

Put DTOs and interfaces in a contracts assembly shared by callers and hosts. The contracts assembly references `MicroModules`.

```csharp
using MicroModules;

public sealed record QuoteRequest(int WeightGrams, string Service);
public sealed record QuoteResult(decimal Price, string Currency);

[ModuleContract("rates", Version = 1)]
public interface IRatesModule
{
    [ModuleOperation("quote", RetrySafe = true)]
    Task<QuoteResult> QuoteAsync(
        QuoteRequest request,
        CancellationToken cancellationToken);
}
```

Implement the interface as an ordinary class. Constructor injection works normally. Keep business validation and authorization in the implementation when they must apply to both local and remote callers.

```csharp
public sealed class RatesModule : IRatesModule
{
    public Task<QuoteResult> QuoteAsync(QuoteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WeightGrams is < 1 or > 20000 || request.Service != "standard")
            throw new ModuleFaultException("invalid-parcel", "Choose standard and a weight from 1 to 20000 grams.");
        return Task.FromResult(new QuoteResult(49m + 10m * decimal.Ceiling(request.WeightGrams / 1000m), "SEK"));
    }
}
```

A consuming module injects `IRatesModule` and calls it. It references the contracts, not Rates' implementation or HTTP details. The full sample adds shipment persistence and notification delivery as ordinary application dependencies.

### Compose a host

Hosts exposing HTTP reference `MicroModules.AspNetCore`. Remote-only callers need only `MicroModules`.

```csharp
builder.Services.AddMicroModules(modules =>
{
    modules.AddLocal<IRatesModule, RatesModule>();
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
    modules.AddRemote<IRatesModule>(builder.Configuration.GetSection("MicroModules:Rates"));
});
```

```json
{
  "MicroModules": {
    "Rates": {
      "Endpoints": ["http://127.0.0.1:5102", "http://127.0.0.1:5103"],
      "AttemptTimeoutSeconds": 2,
      "TotalTimeoutSeconds": 5
    }
  }
}
```

Programmatic registration is also supported:

```csharp
modules.AddRemote<IRatesModule>(options =>
{
    options.Endpoints.Add(new Uri("https://rates.internal"));
    options.Endpoints.Add(new Uri("https://combined.internal"));
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

The parcel sample implements this inside Shipments using Postgres: reserve the operation ID, apply the booking or tracking change, enqueue notifications, and record the result in one transaction. Concurrent duplicates wait for the winning transaction and return its stored result. A reused ID with a different request produces a 409 fault. `servedBy` describes the deployment returning the result; shipment data remains the same on replay. The library has no Postgres dependency.

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

`app.MapMicroModules()` returns a route group, so standard endpoint conventions such as `.RequireAuthorization()` apply. Configure ASP.NET authentication/authorization normally. HTTP endpoint authorization protects remote access; authorization needed for local calls also belongs in module/application logic. The unauthenticated, loopback-bound parcel sample is a local demonstration with fictional customer data.

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

Each sample host opts in during Development, as set by the launch script. Browse:

- BookingApi: http://127.0.0.1:5101/scalar
- ShipmentService: http://127.0.0.1:5102/scalar
- AllInOne: http://127.0.0.1:5103/scalar

BookingApi documents Booking operations; ShipmentService documents Rates, Shipments, and Notifications; AllInOne documents all four modules. Requests through BookingApi invoke its dependencies remotely, while AllInOne invokes the same contracts locally.

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

Parcel tests run as part of the solution. Database tests are skipped unless `PARCEL_TEST_CONNECTION` is set; `scripts/poc.py smoke` supplies the local demo connection automatically. They create and remove only their own randomly named schemas.
