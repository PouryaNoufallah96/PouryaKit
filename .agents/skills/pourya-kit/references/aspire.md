# Aspire resources

`PouryaKit.Aspire`: the containers a Kit Host runs on locally, for an Aspire AppHost and nothing else. PostgreSQL always, Redis for a Host that caches through Redis, RabbitMQ for a Host that sends or receives over RabbitMQ, each on a pinned image with its data in a named volume. Namespace `PouryaKit.Aspire`; it references no Kit package, and `PackageGraphTests` keeps every Aspire package out of every other Kit package's closure, so a Host never takes one.

- [The AppHost](#the-apphost)
- [What each registration adds](#what-each-registration-adds)
- [Running it](#running-it)
- [Testing](#testing)
- [Limits](#limits)

## The AppHost

An AppHost project uses the `Aspire.AppHost.Sdk` and references the Hosts it runs and the package. This is the Proving Sample's `samples/ModularMonolith/src/ModularMonolith.AppHost/ModularMonolith.AppHost.csproj`:

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.6.1">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UserSecretsId>pouryakit-modular-monolith-apphost</UserSecretsId>
    <NoWarn>$(NoWarn);ASPIRE010</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Inventory.Host\Inventory.Host.csproj" />
    <ProjectReference Include="..\ModularMonolith.Host\ModularMonolith.Host.csproj" />
    <PackageReference Include="PouryaKit.Aspire" />
  </ItemGroup>
</Project>
```

The SDK adds `Aspire.Hosting.AppHost` itself: with central package management, give it no `PackageVersion`, or restore fails with NU1009. `ASPIRE010` warns that the AppHost runs without the Aspire CLI bundle; it takes its orchestrator from NuGet instead, so Docker is all it needs.

Its `Program.cs` adds a resource for each part of each Host's composition and gives it to the Host under the connection name the Host reads:

```csharp
using PouryaKit.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPouryaKitPostgres("postgres");
var cache = builder.AddPouryaKitRedis("cache");
var broker = builder.AddPouryaKitRabbitMq("broker");
var shop = postgres.AddDatabase("modular-monolith-db", "modular_monolith");
var inventory = postgres.AddDatabase("inventory-db", "inventory");

builder
    .AddProject<Projects.Inventory_Host>("inventory")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/alive")
    .WithReference(inventory, "app")
    .WithReference(broker)
    .WaitFor(inventory)
    .WaitFor(broker);

builder
    .AddProject<Projects.ModularMonolith_Host>("modular-monolith")
    .WithHttpEndpoint()
    .WithReference(shop, "app")
    .WithReference(cache)
    .WithReference(broker)
    // ...its bearer settings, below...
    .WaitFor(shop)
    .WaitFor(cache)
    .WaitFor(broker);

builder.Build().Run();
```

- **The composition decides, through its connection strings.** An AppHost cannot see a Host's registrations, so the AppHost's author adds Redis only for a Host whose composition registers a Redis cache, and RabbitMQ only for one that calls `UsePouryaKitRabbitMq`. `WithReference(resource, "app")` sets `ConnectionStrings:app`, the name `UsePouryaKitWolverine(connectionName: "app")` reads; `WithReference(cache)` sets `ConnectionStrings:cache` and `WithReference(broker)` `ConnectionStrings:broker`, the names the sample's composition reads. The sample's `ModularMonolith.Host` caches in Redis and sends over RabbitMQ when those are set, so with a broker Inventory runs in `Inventory.Host`, which receives over RabbitMQ and caches nothing.
- **Each Host its own database** on the one server: `AddDatabase(name, databaseName)` creates it when the server is ready.
- **Endpoints.** A Host project without `Properties/launchSettings.json` has none: `WithHttpEndpoint()` gives it one on a free port. `WithHttpHealthCheck("/alive")` waits on a Host that maps the service defaults' liveness path ([host-recipes.md](host-recipes.md#liveness-and-readiness)); the sample's `ModularMonolith.Host` maps none.
- **A Host that authenticates callers** needs its bearer settings, or it fails to start ([security.md](security.md)). The sample passes them as environment variables, the signing key from a secret parameter:

```csharp
var signingKey = builder.AddParameter("bearer-signing-key", secret: true);
// on the ModularMonolith.Host project:
    .WithEnvironment("Authentication__Schemes__Bearer__ValidIssuer", "modular-monolith-apphost")
    .WithEnvironment("Authentication__Schemes__Bearer__ValidAudiences__0", "modular-monolith")
    .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Issuer", "modular-monolith-apphost")
    .WithEnvironment("Authentication__Schemes__Bearer__SigningKeys__0__Value", signingKey)
```

Set the parameter to a base64 key of at least 32 bytes in the AppHost's user secrets (`Parameters:bearer-signing-key`), and sign a caller's token with it, that issuer and that audience.

## What each registration adds

| Call | Resource | Image | Volume target |
|---|---|---|---|
| `AddPouryaKitPostgres(name)` | `PostgresServerResource` | `docker.io/library/postgres:18.6` | `/var/lib/postgresql` |
| `AddPouryaKitRedis(name)` | `RedisResource` | `docker.io/library/redis:8.10.1` | `/data` |
| `AddPouryaKitRabbitMq(name)` | `RabbitMQServerResource` | `docker.io/library/rabbitmq:4.3.6`, no management plugin | `/var/lib/rabbitmq` |

- Each call is Aspire's own `AddPostgres`, `AddRedis` or `AddRabbitMQ` with the image pinned (`WithImage`) and `WithDataVolume()`, so it returns Aspire's resource builder and anything Aspire offers on it still applies.
- The tags are the ones the Kit's Testcontainers fixtures run, so local infrastructure is what the Kit's tests prove. Aspire's own defaults float on a minor version (`18.3`, `8.6`, `4.3`).
- The volume's name is Aspire's, from the AppHost's project and the resource name (`modularmonolith.apphost-<hash>-postgres-data`): it outlives a run, so data persists between runs.
- The package adds nothing else: no dashboard, tracing, logging, metrics or API-reference resource (Jaeger, Zipkin, Tempo, Loki, Kibana, Prometheus, Grafana, Elasticsearch, Swagger). A Host on the service defaults exports its telemetry over OTLP to whatever `OTEL_EXPORTER_OTLP_ENDPOINT` names ([host-recipes.md](host-recipes.md)), and the Kit adds no collector.

## Running it

`dotnet run --project samples/ModularMonolith/src/ModularMonolith.AppHost` starts the containers and both Hosts, and Aspire's dashboard for a developer. It needs Docker and the .NET SDK, no Aspire workload or CLI.

A named volume keeps the credentials it was first initialised with. Aspire generates each server's password as a parameter (`postgres-password`, `cache-password`, `broker-password`) and keeps it in the AppHost's user secrets, which is why the AppHost has a `UserSecretsId`. Set a password yourself, and the volume made with another one refuses it: remove the volume (`docker volume rm`) or keep the first password.

## Testing

`tests/PouryaKit.Aspire.Tests` holds `AppHostTests`, on `Aspire.Hosting.Testing`, whose `DistributedApplicationTestingBuilder` runs the AppHost with the dashboard off:

```csharp
await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
    ["Parameters:postgres-password=apphost-tests", "Parameters:bearer-signing-key=<base64 key>"],
    cancellationToken
);
await using var app = await builder.BuildAsync(cancellationToken);
await app.StartAsync(cancellationToken);
await app.ResourceNotifications.WaitForResourceHealthyAsync("inventory", cancellationToken);
using var inventory = app.CreateHttpClient("inventory");
```

- The model test reads the builder without starting it: every resource's name, as an allow-list, each container's image and its one volume mount, and the resources each Host references.
- The start test runs the containers and both Hosts as processes, waits for each Host, and asks `Inventory.Host`'s `/alive` and an endpoint of `ModularMonolith.Host`, which refuses an anonymous caller with 401 once it serves; the running model is still the allow-list, so no dashboard joined it.
- The test fixes each server's password, so the named volumes one run leaves serve the next.
- The project lives beside `ModularMonolith.Tests` because it shares none of its fixtures: the AppHost starts the Hosts as processes, never in memory.

## Limits

- No resource for Ollama yet: it is #139.
- The registrations know nothing of a Host's composition: an AppHost that adds Redis for a Host that registers no Redis cache runs a container nothing uses.
- `ModularMonolith.Host` maps no liveness path, so the AppHost waits on it only until its process runs.
