# Host recipes

The service defaults of a Host built on PouryaKit: OpenTelemetry that exports the Kit's activities and metrics, liveness and readiness endpoints, service discovery, one standard resilience pipeline for every outgoing client, and Serilog when the Host wants it. They live in the Proving Sample's `ModularMonolith.ServiceDefaults` project, shaped like .NET Aspire's `ServiceDefaults` template. They are template content, not a Kit package and not an Aspire package: the project templates generate the project as `<Name>.ServiceDefaults` and compose all three recipes in their Hosts ([templates.md](templates.md)), and an application owns and changes its copy. Each recipe is optional, and none is needed for the Kit to work.

- [Composing a Host with them](#composing-a-host-with-them)
- [What each recipe does](#what-each-recipe-does)
- [One resilience pipeline](#one-resilience-pipeline)
- [Liveness and readiness](#liveness-and-readiness)
- [Serilog](#serilog)
- [Testing a Host with them](#testing-a-host-with-them)

## Composing a Host with them

The Proving Sample's `Inventory.Host` is a minimal web Host only so that it can serve the health endpoints; it needs no `PouryaKit.Web` registration for them:

```csharp
using Inventory;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddPouryaKitModules(modules => modules.Add<InventoryModule>());
// The recipe exports what the Kit emits; the Kit emits only with this, after the Modules.
builder.Services.AddPouryaKitObservability();
builder.UsePouryaKitWolverine(connectionName: "app");
// Optional: Serilog in place of the logging providers.
builder.AddSerilogDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();
// The JasperFx command line runs the Host, or a command such as codegen write (templates.md).
return await app.RunJasperFxCommands(args);
```

- `AddServiceDefaults()` and `AddSerilogDefaults()` extend any `IHostApplicationBuilder`, in the namespace `Microsoft.Extensions.Hosting` as Aspire's do; `MapDefaultEndpoints()` extends `WebApplication`.
- The recipe exports; the Kit emits. A Host without `AddPouryaKitObservability()` exports no Operation of the Kit, whatever its exporters listen to ([observability.md](observability.md#registering)).
- `ModularMonolith.Host` does not use the recipes, so the Kit's outgoing HTTP stays proven without them (`OutgoingHttpTests`). The Hosts the project templates generate do, with `AddServiceDefaults()`, `AddSerilogDefaults()` and `MapDefaultEndpoints()`, and their `downstream` client adds only `AddPouryaKitOperationContext()`.

## What each recipe does

| Recipe | Method | What it adds |
|---|---|---|
| OpenTelemetry | `ConfigureOpenTelemetry()`, from `AddServiceDefaults()` | Tracing of the sources `PouryaKit` (`OperationTelemetry.SourceName`) and the application's name, ASP.NET Core requests except the health endpoints, and `HttpClient`; metrics of the meter `PouryaKit`, ASP.NET Core, `HttpClient` and the runtime; the OpenTelemetry logging provider. OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, otherwise nothing leaves the process. |
| Health checks | `AddDefaultHealthChecks()`, from `AddServiceDefaults()` | `self`, tagged `live`, and `databases`, which asks each DbContext the Host registered whether its database answers ([below](#liveness-and-readiness)). |
| Service discovery | from `AddServiceDefaults()` | `AddServiceDiscovery()`, and its handler on every `HttpClient`. |
| HTTP resilience | from `AddServiceDefaults()` | `AddPouryaKitResilience()` on every `HttpClient` through `ConfigureHttpClientDefaults` ([below](#one-resilience-pipeline)). |
| Health endpoints | `MapDefaultEndpoints()` | `/health`, every check, and `/alive`, the `live` checks only, both anonymous, in Development only as in Aspire's template: read https://aka.ms/aspire/healthchecks before mapping them in other environments. |
| Serilog | `AddSerilogDefaults()`, opted into by the Host | A console sink, an OTLP sink when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, the `Serilog` configuration section, and every `ILogEventSink` registered as a service ([below](#serilog)). |

- The tracing adds the source `PouryaKit`, not `Wolverine`. Add `.AddSource("Wolverine")` to export Wolverine's spans as well; the Kit's trace holds either way, in one Host and across RabbitMQ ([observability.md](observability.md#the-trace-through-delivery)).
- The packages are pinned in `Directory.Packages.props`: `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.19.1, `OpenTelemetry.Instrumentation.AspNetCore`, `.Http` and `.Runtime` 1.19.0, `Microsoft.Extensions.ServiceDiscovery` 10.10.0, `Serilog.AspNetCore` 10.0.0, `Serilog.Sinks.Console` 6.1.1 and `Serilog.Sinks.OpenTelemetry` 4.2.0, beside `Microsoft.EntityFrameworkCore.Relational` for the readiness check. No Aspire package. Health checks are in the ASP.NET Core shared framework.

## One resilience pipeline

Aspire's template adds `AddStandardResilienceHandler()` to every client, which also retries POST. The recipe adds `AddPouryaKitResilience()` there instead: the same standard handler, retrying GET, HEAD, OPTIONS and TRACE only ([web.md](web.md#calls-between-kit-hosts)). The defaults are applied before any client's own configuration, whatever the registration order.

- A client that the defaults suit adds only `AddPouryaKitOperationContext()`: one pipeline, three retries of a safe method, a write sent once.
- A client that needs other limits, or `HttpReplay.AllRequests`, replaces the default pipeline:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PouryaKit.Web;

var downstream = builder.Services.AddHttpClient("downstream");
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental in Microsoft.Extensions.Http.Resilience.
downstream.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
downstream.AddPouryaKitResilience().Configure(options => options.Retry.MaxRetryAttempts = 2);
downstream.AddPouryaKitOperationContext();
```

- `AddPouryaKitResilience()` on a client of a Host with these defaults, without removing theirs first, would stack a second pipeline on the first, each outer attempt making every inner one. The Kit refuses it: once the client's handler chain is built, after every configuration callback and `RemoveAllResilienceHandlers()` ran, a chain with two resilience handlers throws an `InvalidOperationException` naming `RemoveAllResilienceHandlers()`, each time the client is created (`MisconfiguredHostTests`).
- A client that replaces the default pipeline has a name. The default pipeline is one for all clients that keep it: `ConfigureHttpClientDefaults` registers it under one name, `-standard`, since its builder has no client name, and the client registered with an empty name (`AddHttpClient(string.Empty)`) gets the same name. Its own pipeline would be the default one, options and replay policy included, so `HttpReplay.AllRequests` there would make every client that keeps the defaults replay writes. `AddPouryaKitResilience` refuses that client with an `ArgumentException` that says to name it (`HostRecipeTests`).
- One pipeline name also means one circuit breaker, which counts the failures of every client that keeps the defaults; this follows from the shared name and is not tested. A client whose downstream must not trip the others' breaker replaces the pipeline as above.

## Liveness and readiness

- `/alive` asks only the checks tagged `live`, so it answers `Healthy` while PostgreSQL or RabbitMQ is down. Liveness never depends on anything outside the process: an orchestrator that restarts a Host whose database is unreachable gains nothing.
- `/health` asks every check, `databases` included: it answers 503 `Unhealthy` while a database the Host's DbContexts use does not answer, and `Healthy` again once it does. A Module's DbContext is internal to it, so the check finds each through its registered `DbContextOptions` instead of by type, and asks it what `AddDbContextCheck` asks, `Database.CanConnectAsync()`, which runs `SELECT 1`.
- RabbitMQ has no check of its own: WolverineFx 6.46 registers none. A broker that is down shows in Wolverine's own reconnection, not in `/health`.

## Serilog

`AddSerilogDefaults()` replaces the logger factory with Serilog's. Every log entry, the Kit's included, then goes to Serilog's sinks only: a logging provider added to `builder.Logging`, the OpenTelemetry provider of `ConfigureOpenTelemetry()` and a test's provider alike, receives nothing. That is why the recipe has its own OTLP sink, and why it reads sinks from the services: register an `ILogEventSink` to receive the Host's logs.

```csharp
builder.AddSerilogDefaults();
builder.Services.AddSingleton<Serilog.Core.ILogEventSink>(sink);
```

## Testing a Host with them

The Proving Sample's `HostRecipeTests` (`tests/ModularMonolith.Tests`) are the pattern:

- **Export without a backend.** Add an exporter of your own to the recipe's providers, `services.ConfigureOpenTelemetryTracerProvider(t => t.AddProcessor(new SimpleActivityExportProcessor(exporter)))` and `services.ConfigureOpenTelemetryMeterProvider(m => m.AddReader(new BaseExportingMetricReader(exporter)))`, then `MeterProvider.ForceFlush()` before asserting the `pouryakit.operation.duration` points. The providers are process-wide listeners: filter by trace id or by the `pouryakit.module` tag.
- **A database that stops answering.** `alter database <name> with allow_connections false` and `pg_terminate_backend` of its sessions, from a connection to another database, then `allow_connections true` before the Host stops. It touches one test's database only, unlike stopping the shared container.
- **Logs under Serilog.** Register an `ILogEventSink` as above: the fixture's `RecordedLogs` provider receives nothing once Serilog is the factory.
- **Outgoing HTTP.** A Web Host of its own with `AddServiceDefaults()`, `UseTestServer()` and the client, against WireMock.Net. A 503 with `Retry-After: 0` makes the standard pipeline retry at once, so its default limit is counted in milliseconds; `Retry-After: 1` holds the first retry for a second, and a test that cancels once the first attempt reached WireMock, rather than on a timer a slow first request can outrun, sees that retry never sent.
- **Telemetry and logs after a delivery.** The delivery's rows commit before its Operation logs, records its duration and ends its activity. Wait for the message store to settle (`BrokerEnvironment.WaitUntilSettledAsync`: Wolverine marks the envelope handled once the handler returned) before flushing the meter or reading a sink.
