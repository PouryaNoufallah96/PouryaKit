# Aspire resources

`PouryaKit.Aspire`: the containers a Kit Host runs on locally, for an Aspire AppHost and nothing else. PostgreSQL always, Redis for a Host that caches through Redis, RabbitMQ for a Host that sends or receives over RabbitMQ, Ollama for a Host whose agent runs locally, each on a pinned image with its data in a named volume. Namespace `PouryaKit.Aspire`; it references no Kit package, and `PackageGraphTests` keeps every Aspire package out of every other Kit package's closure, so no Kit package brings one into a Host. Keeping the package itself out of a Host's own project file is the Host author's part.

- [The AppHost](#the-apphost)
- [A local agent](#a-local-agent)
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

Its `Properties/launchSettings.json` has one profile that sets `DOTNET_ENVIRONMENT` to `Development` and opens no browser. The environment matters: the AppHost reads its user secrets only in Development, and without them it generates new server passwords on every run, which the volumes the first run made then refuse.

The SDK adds `Aspire.Hosting.AppHost` itself: with central package management, give it no `PackageVersion`, or restore fails with NU1009. `ASPIRE010` warns that the AppHost runs without the Aspire CLI bundle; it takes its orchestrator from NuGet instead, so Docker is all it needs.

Its `Program.cs` adds a resource for each part of each Host's composition and gives it to the Host under the connection name the Host reads:

```csharp
using PouryaKit.Aspire;

var builder = DistributedApplication.CreateBuilder(AppHostOptions.Create(args));

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

- **Headless.** `AppHostOptions.Create(args)` (`AppHostOptions.cs` beside `Program.cs`) returns `DistributedApplicationOptions` with `DisableDashboard = true`: no dashboard, under `dotnet run` or in the tests. A developer who wants Aspire's dashboard locally sets it to `false` in their copy; the AppHost then logs the dashboard's login URL.
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

## A local agent

A Host whose agent calls a model ([ai.md](ai.md)) can run it locally on Ollama. The Proving Sample's AppHost adds it only when it runs with `LocalAgent=true`, an argument or the environment variable `LocalAgent`, since the image and the model are large and the Host starts without them:

```csharp
var shopHost = builder.AddProject<Projects.ModularMonolith_Host>("modular-monolith") /* ...as above... */;

if (bool.TryParse(builder.Configuration["LocalAgent"], out var localAgent) && localAgent)
{
    const string Model = "qwen2.5:0.5b";
    var ollama = builder.AddPouryaKitOllama("ollama", Model);
    shopHost
        .WithEnvironment("PouryaKit__AI__Provider", "Ollama")
        .WithEnvironment("PouryaKit__AI__Endpoint", ReferenceExpression.Create($"{ollama.GetEndpoint("http")}/v1"))
        .WithEnvironment("PouryaKit__AI__Model", Model)
        .WaitFor(ollama);
}
```

- `AddPouryaKitOllama(name, model)` adds a plain container with an HTTP endpoint named `http` on Ollama's port 11434. A container resource has no connection string, so the Host gets its `PouryaKit:AI` section as environment variables: the endpoint with `/v1`, Ollama's OpenAI-compatible API, and the model the registration serves.
- Its health check is healthy only once the server has the model: on the first start it pulls the model into the volume, a download of several hundred megabytes, and `WaitFor` holds the Host until then. Aspire awaits a check with no deadline of its own, so each request has one, 10 seconds to say whether the server has the model and 30 minutes for the pull: an unreachable or silent server, or a pull that fails or outlasts its deadline, leaves it unhealthy, and Aspire checks again. Later starts find the model in the volume.
- No Aspire package for Ollama: the Aspire Community Toolkit's Ollama hosting package brings `OllamaSharp`, whose licence is a file, which the licence test refuses (spec #115's A1).
- The sample's agent is `POST /catalog/v2/products/ask` (or the unversioned path with the `api-version` header) (body `{"question": "..."}`), authenticated and tenant-scoped like every Catalog endpoint; send it a bearer token signed with the `bearer-signing-key` parameter.

## What each registration adds

| Call | Resource | Image | Volume target |
|---|---|---|---|
| `AddPouryaKitPostgres(name)` | `PostgresServerResource` | `docker.io/library/postgres:18.6` | `/var/lib/postgresql` |
| `AddPouryaKitRedis(name)` | `RedisResource` | `docker.io/library/redis:8.10.1` | `/data` |
| `AddPouryaKitRabbitMq(name)` | `RabbitMQServerResource` | `docker.io/library/rabbitmq:4.3.6`, no management plugin | `/var/lib/rabbitmq` |
| `AddPouryaKitOllama(name, model)` | `ContainerResource`, healthy once it serves `model` | `docker.io/ollama/ollama:0.40.6` | `/root/.ollama` |

- Each call but Ollama's is Aspire's own `AddPostgres`, `AddRedis` or `AddRabbitMQ` with the image pinned (`WithImage`) and `WithDataVolume()`, so it returns Aspire's resource builder and anything Aspire offers on it still applies; Ollama's is `AddContainer` with a volume Aspire names the same way.
- The tags are the ones the Kit's Testcontainers fixtures and its Ollama probe run, so local infrastructure is what the Kit's tests prove. Aspire's own defaults float on a minor version (`18.3`, `8.6`, `4.3`).
- The volume's name is Aspire's, from the AppHost's project and the resource name (`modularmonolith.apphost-<hash>-postgres-data`): it outlives a run, so data persists between runs.
- The package adds nothing else: no dashboard, tracing, logging, metrics or API-reference resource (Jaeger, Zipkin, Tempo, Loki, Kibana, Prometheus, Grafana, Elasticsearch, Swagger). A Host on the service defaults exports its telemetry over OTLP to whatever `OTEL_EXPORTER_OTLP_ENDPOINT` names ([host-recipes.md](host-recipes.md)), and the Kit adds no collector.

## Running it

`dotnet run --project samples/ModularMonolith/src/ModularMonolith.AppHost` starts the containers and both Hosts, headless. It needs Docker and the .NET SDK, no Aspire workload or CLI, and the `bearer-signing-key` parameter set first, in the AppHost's user secrets or as the environment variable `Parameters__bearer-signing-key`. Add `LocalAgent=true` after `--` for Ollama and the agent's model.

A named volume keeps the credentials it was first initialised with. Aspire generates each server's password as a parameter (`postgres-password`, `cache-password`, `broker-password`) and keeps it in the AppHost's user secrets, which is why the AppHost has a `UserSecretsId` and runs in Development, where it reads them back. Set a password yourself, and the volume made with another one refuses it: remove the volume (`docker volume rm`) or keep the first password.

## Testing

`tests/PouryaKit.Aspire.Tests` holds `AppHostTests`, on `Aspire.Hosting.Testing`, whose `DistributedApplicationTestingBuilder` runs the AppHost. The testing builder replaces the AppHost's options with its own, dashboard off whatever the AppHost says, so the start test passes the sample's own choice, `AppHostOptions.Create([]).DisableDashboard`, to the builder's options callback:

```csharp
await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
    ["Parameters:postgres-password=apphost-tests", "Parameters:bearer-signing-key=<base64 key>"],
    (options, _) => options.DisableDashboard = AppHostOptions.Create([]).DisableDashboard,
    cancellationToken
);
await using var app = await builder.BuildAsync(cancellationToken);
await app.StartAsync(cancellationToken);
await app.ResourceNotifications.WaitForResourceHealthyAsync("inventory", cancellationToken);
using var inventory = app.CreateHttpClient("inventory");
```

- The model test reads the builder without starting it: every resource's name, as an allow-list, each container's image and its one volume mount, and the resources each Host references; with no `LocalAgent`, no Ollama. A second model test builds it with `LocalAgent=true`: the allow-list gains `ollama` alone, on its pinned image and named volume, with its model check, and `ModularMonolith.Host` waits for it with its `PouryaKit__AI__Provider`, `__Endpoint` and `__Model` set, which `Inventory.Host` is not given. Neither starts anything, so no image or model is downloaded.
- The start test runs the containers and both Hosts as processes, waits for each Host, and asks `Inventory.Host`'s `/alive`, which its health check already answered, and an endpoint of `ModularMonolith.Host`, which refuses an anonymous caller with 401 once it serves; that Host has no health check, so its resource is healthy once its process runs, and the test asks again until it answers, within its timeout. The running model is still the allow-list, so no dashboard joined it.
- `OllamaModelHealthCheckTests` run the model check against a loopback server that accepts and never answers, once for `/api/show` and once for the pull after a 404: each check returns unhealthy when its deadline passes.
- The local agent's start test runs only with `POURYAKIT_OLLAMA_PROBE=1`, the variable of `AiTests`' Ollama probe: it starts the AppHost with `LocalAgent=true`, waits for Ollama's model and the Host, and posts a question to the agent with a bearer token it signs with the test's key, expecting 200 and the model's text. A first run pulls the 7 GB image and the model; its timeout is 30 minutes.
- The tests fix each server's password, and the start test moves each volume to a name ending `-tests`: its volumes serve its next run, and a developer's, made with the passwords the AppHost generated, are never opened with the test's, which would fail every health check.
- The project lives beside `ModularMonolith.Tests` because it shares none of its fixtures: the AppHost starts the Hosts as processes, never in memory.

## Limits

- The model is pulled only when the resource starts, by the health check; a model the AppHost stops serving stays in the volume until it is removed.
- The registrations know nothing of a Host's composition: an AppHost that adds Redis for a Host that registers no Redis cache runs a container nothing uses.
- `ModularMonolith.Host` maps no liveness path, so the AppHost waits on it only until its process runs.
