# Project templates

`PouryaKit.Templates`, a `dotnet new` template package: a new application starts from the Proving Sample's business code in one of two compositions. The monolith is the single-Module start, which grows into the modular monolith by adding `PouryaKit.Modularity` ([below](#growing-the-monolith-into-a-modular-monolith)); the modular monolith is several Modules in one Host, each reached through its Gateway. The templates are generated from the sample, never maintained beside it.

- [Install and generate](#install-and-generate)
- [What each composition composes](#what-each-composition-composes)
- [Growing the monolith into a modular monolith](#growing-the-monolith-into-a-modular-monolith)
- [Running the generated Host](#running-the-generated-host)
- [The smoke test](#the-smoke-test)
- [The optional recipes](#the-optional-recipes)
- [Pre-generating Wolverine's code for production](#pre-generating-wolverines-code-for-production)
- [How the package is built](#how-the-package-is-built)

## Install and generate

```bash
# From the private feed (publication.md) or a validated local packages/ folder; the version is the Kit's.
dotnet new install PouryaKit.Templates::0.1.0-preview.0.HEIGHT --add-source <feed>

dotnet new pouryakit-monolith -n Acme.Shop
dotnet new pouryakit-modular-monolith -n Acme.Shop
```

- `-n` is the project's name as you type it, such as `Acme.Shop`, `acme-shop`, `acme_shop` or `My Shop`. Folders and files carry it as typed, and so do the paths the solution and the project references name: `src/acme-shop.Host/acme-shop.Host.csproj`. Code carries the engine's namespace-safe form of it: `acme-shop` becomes `namespace acme_shop.Host` and `acme_shop.Tests`, `My Shop` becomes `My_Shop`, and `Acme.Shop` stays as it is. The broker queue takes the derived service name ([below](#the-optional-recipes)): `acme-shop` for the first three, `my-shop` for the last.
- Each template generates a solution in a folder named after `-n`: `Acme.Shop.slnx`, `src/Acme.Shop.Host`, `src/Acme.Shop.ServiceDefaults`, the Modules under `src/Modules/` with their sample names, and `tests/Acme.Shop.Tests`. Without `-n` it takes the output folder's name.
- `dotnet new` restores the solution once it is generated; `--no-restore` skips that. The restore needs a NuGet source with the Kit's packages.
- `Directory.Packages.props` pins every Kit package at `$(PouryaKitVersion)`, the version the template package was packed with, and every third-party package at the version the Kit builds and tests with. Upgrade the Kit by changing `PouryaKitVersion`.
- `global.json` pins the Kit's SDK and Microsoft.Testing.Platform; `.config/dotnet-tools.json` holds `dotnet-ef`.

## What each composition composes

| | `pouryakit-monolith` | `pouryakit-modular-monolith` |
|---|---|---|
| Role | The single-Module start: one Module's data and rules, which grows into the modular monolith when a second Module arrives | Several Modules in one Host, each reached through its Gateway |
| Business code | Catalog and `Catalog.Contracts` | Catalog, `Catalog.Contracts`, Billing and Inventory |
| Composition Root | `CatalogModule`, a static class whose `services.AddCatalog()` is a plain registration on the Host's services | Each Module's `IModuleDefinition`, added with `AddPouryaKitModules` |
| Endpoint adapters | Each Feature's endpoint sends through `IDispatcher` | Each Feature's endpoint sends through `IGatewayProcessor<CatalogModule>` |
| Kit packages | Every Wave 2 Capability that applies, without `PouryaKit.Modularity` | Every Wave 2 Capability, with `PouryaKit.Modularity` |
| The Host's checks | Container validation, the composition checks and the startup checks from `AddPouryaKitCompositionChecks` | The same, from `AddPouryaKitModules` and `UsePouryaKitWolverine`, both of which call it |

- **The same business code.** Aggregates, commands, queries, handlers, validators, the repository, Dapper reads and every event handler are the sample's own files in both. Only the Composition Root and the endpoint adapters differ, and in the modular monolith they are the sample's own too.
- **The monolith is one Module in all but name** (plan I.1). A persistence package adds its `IUnitOfWork` with `Add`, and only Modularity's `ModuleBuilder` keys each Module's own, so a Host without Modularity holds one Unit of Work: one Module's data. The monolith therefore takes Catalog alone; Billing and Inventory stay in the modular monolith. Its Operation names no Module, its Unit of Work is the Host's, and Catalog publishes with no Module, so its Integration Events still reach another Host over RabbitMQ when a broker is configured. Because the request scope holds that Unit of Work, an endpoint or a service could take a repository there, which a Module of a Host of Modules cannot: keep the repository in handlers and send a command ([persistence.md](persistence.md#the-repository)).
- **The Host composition**, in `AppHost.cs`, is the same in both: `AppHost.CreateBuilder(args)` composes it and `AppHost.Build(builder)` builds it with its request pipeline, so `Program.cs` and the smoke test compose the one Host, as the Proving Sample's `ModularMonolithHost` does. It holds JWT bearer authentication from `Authentication:Schemes:Bearer`, authenticated-by-default HTTP, trusted peers, CORS, compression and the rate limit per Actor, the API versions and OpenAPI, idempotency key retention, the `downstream` HTTP client with the Operation Context, permissions from the caller's `permission` claims (`CallerPermissions`), Observability, Caching with Redis when `ConnectionStrings:cache` is set, Wolverine over EF Core on `ConnectionStrings:app`, RabbitMQ when `ConnectionStrings:broker` is set, and the service defaults recipes ([host-recipes.md](host-recipes.md)): `AddServiceDefaults()`, `AddSerilogDefaults()` and `MapDefaultEndpoints()`. The modular monolith's Inventory reconciles stock at `Inventory:ReconcileStock:Every`.
- **Calls between Modules stay in-process**: Integration Events and Snapshots through the Host's Outbox, a question through the owning Module's Contracts. Never HTTP between Modules of one Host.
- **The same composition and startup checks in both.** The monolith calls Cqrs's Host entry, the one `AddPouryaKitModules` calls, so a declared permission without its check, a cacheable query without caching or a command without a Unit of Work stops the Host when it is built, an endpoint never mapped stops it when it starts, and its container is validated in every environment ([modules.md](modules.md#the-hosts-checks)). `UsePouryaKitWolverine` calls the same entry; the monolith calls it last, after any `ConfigureContainer` of its own:

```csharp
builder.AddPouryaKitCompositionChecks();
```

- **The monolith names its assembly; the modular monolith infers it.** The monolith's `AddCatalog()` runs on the Host's own services, which are no Module's builder, so its registrations name `typeof(CatalogModule).Assembly` and its declarations belong to the application. Each modular Composition Root registers on its Module's builder with no assembly and no Module ([modules.md](modules.md#the-composition-root)).

## Growing the monolith into a modular monolith

The monolith is where a project of one Module starts, not a limit on it. When a second Module arrives, the project adds `PouryaKit.Modularity` and becomes the modular monolith; no aggregate, command, query, handler, validator, repository, read or event handler changes, since both templates generate Catalog's business code from the same sample files. A Module that later runs in its own process is the service composition, Wave 4's template; the Proving Sample's `Inventory.Host` runs one over RabbitMQ ([rabbitmq.md](rabbitmq.md)).

Generated with one name from one template package, the two solutions differ in twelve files, and the modular monolith adds Billing and Inventory, `CatalogProducts.cs` and its cross-Module test. Nine of the twelve change when Catalog becomes a Module:

1. `Directory.Packages.props`: `PouryaKit.Modularity` at `$(PouryaKitVersion)`.
2. `src/Modules/Catalog/Catalog/Catalog.csproj`: a `PackageReference` to `PouryaKit.Modularity`.
3. `CatalogModule.cs`: the static class whose `services.AddCatalog()` registers on the Host's services becomes `public sealed class CatalogModule : IModuleDefinition`, whose `Configure(ModuleBuilder module)` makes the same calls on `module`, without the `typeof(CatalogModule).Assembly` each registration names in the monolith. On a Module's builder the Kit scans the Module's own assembly, and every declaration, `Publish`, `PublishSnapshot` and `AddPouryaKitNotificationHandlers` included, belongs to the Module with nothing added ([modules.md](modules.md#the-composition-root)).
4. The five endpoints, `CreateCategoryEndpoint`, `CreateProductEndpoint`, `GetProductEndpoint`, `ListProductsEndpoint` and `RenameProductEndpoint`: each takes `OperationContext` and `IGatewayProcessor<CatalogModule>` in place of `IDispatcher`, and passes the context to `SendAsync` or `QueryAsync` ([web.md](web.md#endpoints)).
5. `src/Acme.Shop.Host/AppHost.cs`: `builder.Services.AddCatalog()` becomes `builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>())`. `AddPouryaKitModules` calls `AddPouryaKitCompositionChecks()`, so the monolith's own call can go; calling it again changes nothing.

The other three, the `.slnx`, the Host's csproj and `appsettings.json`, differ by Billing and Inventory: a second Module adds its project, its row in the solution, its `ProjectReference` in the Host, its `modules.Add<…>()` and its own configuration. `CatalogProducts.cs` and its registration, Catalog's answer to `ICatalogProducts` (in `Catalog.Contracts`, which both templates generate), come back when another Module asks Catalog a question ([modules.md](modules.md#asking-another-module-a-question)).

The two traps this path had are closed:

- **A declaration without its Module.** In the monolith a declaration belongs to the application. On a Module's builder it belongs to the Module with no argument (#61), and one left on `builder.Services` in a Host of Modules fails the Host when it is built, naming its form on the Module's builder and the form that names the Module ([modules.md](modules.md#the-hosts-checks)). Until #61 a Composition Root passed `typeof(CatalogModule)` to each declaration by hand, and a forgotten one failed only at its first publish.
- **A `ConfigureContainer` of the Host's own.** The monolith's `AppHost.cs` holds none, and calls `AddPouryaKitCompositionChecks()` last, so one the Host adds before it keeps the checks installed. In a Host of Modules, a `ConfigureContainer` after `AddPouryaKitModules` replaces the container the checks install, and the Host fails when it starts, naming the call (#61, #62). Until #61 the monolith's `Program.cs` carried a `ConfigureContainer` block, and a Host grown from it lost its container validation and its checks without a word.

## Running the generated Host

```bash
cd Acme.Shop
dotnet tool restore
# ConnectionStrings:app in src/Acme.Shop.Host/appsettings.json names the PostgreSQL database.
dotnet ef database update --project src/Modules/Catalog/Catalog --startup-project src/Acme.Shop.Host --context CatalogDbContext
# The modular monolith also migrates Billing (BillingDbContext) and Inventory (InventoryDbContext) the same way.
dotnet user-jwts create --project src/Acme.Shop.Host --claim tenant=acme
dotnet run --project src/Acme.Shop.Host
```

- The Host applies no migrations at startup; they are the Host's, as [persistence.md](persistence.md) says.
- Without `Authentication:Schemes:Bearer` the Host does not start. `dotnet user-jwts create --claim tenant=<name>` fills it for development and prints a token; Catalog is tenant-scoped in both templates ([multi-tenancy.md](multi-tenancy.md)), so a token without the `tenant` claim gets 403 `Tenant.Required` from every Catalog command and query; in production, configure the issuer, audiences and signing keys of the real identity provider.
- The `http` launch profile runs in Development on `http://localhost:5080`, where `/health`, the OpenAPI documents and the Scalar reference are mapped, beside `/alive`, which every environment maps. A token's `sub` is the Actor, its `tenant` the tenant, and its `permission` claims the Actor's permissions.

## The smoke test

`tests/Acme.Shop.Tests` hosts the generated Host through `PouryaKit.Testing` ([testing.md](testing.md)): its `App` is a `HostFixture` whose `CreateHost` composes the Host with `AppHost.CreateBuilder` and `AppHost.Build`, served in memory with `UseTestServer()` (`Microsoft.AspNetCore.TestHost`), so the fixture runs PostgreSQL in Testcontainers (Docker must run), applies every DbContext's migrations, starts the Host and resets the data before each `HostTest<App>`. The tests call it with bearer tokens of a test issuer configured through `Authentication:Schemes:Bearer`. `PouryaKit.Testing` depends on `PouryaKit.Modularity` for its Gateway members, which a monolith's tests never call, so the monolith's test project restores Modularity while its Host does not: the package validation allows both in the test project's closure only, and refuses Modularity in the monolith Host's closure and its Release publish.

- A category and a product created over HTTP, the product with an `Idempotency-Key`: 201 with its id and versioned location; the same key again answers the same status, body and location, and the filtered, sorted list still has one product.
- `/alive` answers 200 `Healthy` through the service defaults, to a caller without a token, from a second Host composed in Production, as it is deployed, and started with `HostFixture.StartHostAsync`; `/health` answers 401 there, since the service defaults map it in Development only. That liveness asks nothing outside the process, so it stays healthy while the database is down, is the recipe's own proof (`HostRecipeTests` in the Proving Sample, #52).
- Composed with a `ConnectionStrings:broker` value, the Host joins the broker under its derived service name (below). The test builds it and never starts it, so no broker is needed.
- With the permission checks removed from the registrations, the Host does not build: `RemoveProductsCommand declares RequiresPermissionAttribute, but its check is not registered`.
- The modular monolith adds one cross-Module effect: a product Catalog creates opens its stock item in Inventory's schema.

```bash
dotnet test --project tests/Acme.Shop.Tests/Acme.Shop.Tests.csproj
```

## The optional recipes

- `src/Acme.Shop.ServiceDefaults` is the application's own copy of the service defaults recipes: OpenTelemetry with OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, liveness and readiness, service discovery, one resilience pipeline for every `HttpClient`, and Serilog. Change or drop any of them there ([host-recipes.md](host-recipes.md)).
- The `downstream` client takes its resilience from the defaults and adds only `AddPouryaKitOperationContext()`. A client that needs other limits removes the default pipeline first, as [host-recipes.md](host-recipes.md#one-resilience-pipeline) shows; adding `AddPouryaKitResilience()` on top would stack a second pipeline, which the Kit refuses when the client is created.
- `/alive` asks nothing outside the process and is mapped in every environment, anonymous and exempt from the rate limit, for an orchestrator's liveness probe. `/health` also asks each database and is mapped in Development only. Why `/alive` carries no request timeout or output cache, and what a Host that maps more takes from Aspire's guidance, is in [host-recipes.md](host-recipes.md#liveness-and-readiness).
- Redis (`ConnectionStrings:cache`) and RabbitMQ (`ConnectionStrings:broker`) are off until configured. With a broker, the Host's queue, its service name on the broker, is the project's name lower-cased, each run of characters other than letters and digits a hyphen: `-n Acme.Shop` gives `acme-shop`. The template's `serviceName` symbol derives it, because a queue name takes only letters and digits in segments joined by single dots or hyphens, and the Host refuses any other name as it is composed (`UsePouryaKitRabbitMq`). A name with no letter or digit gives none, and another kind of Host on the same broker needs a name of its own; another Host that receives Catalog's events declares them as [rabbitmq.md](rabbitmq.md#a-host-that-receives) shows.
- CORS policies (`PouryaKit:Web:Cors`), compression (`PouryaKit:Web:Compression`), the rate limit (`PouryaKit:Web:RateLimit`), trusted peers (`PouryaKit:TrustedPeers`) and idempotency key retention (`PouryaKit:Idempotency`) are read from configuration; none is set.

## Pre-generating Wolverine's code for production

Outside Development, Wolverine loads its generated handler code from the Host's assembly instead of compiling it when the Host starts: `UsePouryaKitWolverine` sets `TypeLoadMode.Static` there ([messaging.md](messaging.md#declaring-handlers-and-running-deliveries)). The generated Host follows Wolverine's code generation guide (`JasperFx/wolverine`, `docs/guide/codegen.md`) and ADR 0004:

- `Program.cs` is `return await AppHost.Build(AppHost.CreateBuilder(args)).RunJasperFxCommands(args);`, so the Host's command line is JasperFx's: `dotnet run` runs the Host, `dotnet run -- codegen write` writes Wolverine's generated code into `src/Acme.Shop.Host/Internal/Generated`, `codegen preview` prints it, `check-env` runs the environment checks and `help` lists the rest. Configure the Host through settings files, user secrets or the environment: an argument that is no JasperFx command, such as `--ConnectionStrings:app=...`, stops it with "Invalid usage".
- The Host references `WolverineFx.RuntimeCompilation` in Debug builds only (`<ItemGroup Condition="'$(Configuration)' == 'Debug'">`): Development compiles at run time, and a Release publish carries neither `Wolverine.RuntimeCompilation.dll` nor `JasperFx.RuntimeCompiler.dll`. Roslyn itself stays in the publish through `Microsoft.EntityFrameworkCore.Design`, which the EF Core outbox brings. The test project gets the package through `PouryaKit.Testing`, since it hosts the Host in Development from any build.
- The step before a release, `codegen write`, builds the Host and never starts it, so it needs no database and no runtime compiler; then publish:

```bash
dotnet run --project src/Acme.Shop.Host -- codegen write
dotnet publish src/Acme.Shop.Host -c Release -o publish
```

- Publish with its own restore, or after `dotnet restore -p:Configuration=Release`. A restore that evaluated Debug, such as a plain `dotnet restore`, lists the compiler, and a Release build with `--no-restore` after it carries it again.
- For a Kit Host today the step writes one file, `Internal/Generated/WolverineHandlers/GeneratedHandlerRegistry.cs`, an empty handler registry: the Kit's deliveries need no generated code. Without it the Host still starts in Production, and Wolverine logs that it falls back to a scan; `check-env` fails on it. The step matters once the application adds Wolverine handlers of its own through the `configure` hook of `UsePouryaKitWolverine`: each handler chain then has a generated type, and a Host published without it does not start (`MissingPreBuiltTypesException`).
- Commit `Internal/Generated`, as Wolverine's guide does, and write it again whenever a Wolverine handler changes; or run the step in the build, before the publish.
- Wolverine still creates its message storage when the Host starts, in every environment.

## How the package is built

- `src/PouryaKit.Templates` packs the templates' own files, the Hosts' `Program.cs` and `AppHost.cs`, project files, solution, central versions and smoke test, and packs the business code from `samples/ModularMonolith/src` when the package is built, so no copy of a sample file is kept. The package's version, the Kit's MinVer version, is written into each template's `Directory.Packages.props` as it is packed.
- The `sourceName` is the sample's own `ModularMonolith`, a valid identifier, so its form as typed and its namespace-safe form are one string, and the engine writes the namespace-safe form of `-n` everywhere in content while it renames files and folders with the name as typed. The template's own solution and project files therefore name each project's path through the token `PouryaKitAppName`, which the `projectName` symbol (`derived` from `name` with the `identity` form) replaces with the name as typed, as Aspire's starter template gets from a source name whose two forms differ. The packed sample files keep `ModularMonolith` for their namespaces.
- The monolith's `template.json` excludes the sample's `*Module.cs`, `*Endpoint.cs` and `CatalogProducts.cs` and supplies its own Composition Root and endpoints at the same paths. A new Gateway endpoint in the sample fails `TemplateTests` until the monolith has its own.
- `TemplateTests` (`tests/PouryaKit.ArchitectureTests`) asserts that the template content holds no copy of a sample file, that nothing the monolith generates names Modularity, that each excluded file is replaced, that each template project of a Module, of Catalog's Contracts and of the ServiceDefaults references what the sample's project at its place does, Modularity aside in the monolith (each Host project is its template's own), that the central versions are the Kit's, that each Host passes the derived `serviceName`, and that each solution and project file names its projects' paths through `projectName`'s token, never the `sourceName`, and names only projects the template generates; each symbol is the same in both templates. It also asserts that each file the monolith replaces makes its sample file's calls in its order, with the same routes, API versions, anonymous Actor, `RequireIdempotencyKey`, endpoint names and registrations, once the composition's own differences are read away (the Operation Context the sample's endpoints pass to the Gateway, the sample-only `ICatalogProducts` registration, the assembly the monolith names); that each template Host builds the sample Host's pipeline, the same `app.Use…` and `app.Map…` calls in the same order, `MapDefaultEndpoints` apart; that each generated Host calls `UseRouting()` before `UseRateLimiter()`, the order the rate-limit exemption of `/alive` needs; and that none of the monolith's own files, its Composition Root, its endpoints, `Program.cs`, `AppHost.cs` and the smoke test, takes a repository, `IRepository<,>` or an interface the Proving Sample derives from it, as a parameter, a field or a local or from a service provider: a change made to an aggregate outside a command is one the Kit cannot refuse ([persistence.md](persistence.md#the-repository)). A registration takes none. The rule reads those files as text and judges nothing a project adds after `dotnet new`.
- `scripts/packaging/validate.py` installs the produced package into a hive of its own, generates both templates, the monolith with `-n Acme.Shop` and the modular monolith with `-n acme-shop`, restores and builds them from the produced packages only, refuses `PouryaKit.Modularity` in the monolith's runtime closure and `PouryaKit.Testing` in both, every project's but the test project's, which restores `PouryaKit.Testing` and, through it, Modularity, runs each generated smoke test, then runs `codegen write` on each generated Host and checks its Release publish: the Host built with the generated code, without the runtime compiler and without an assembly of a package its runtime closure refuses. The Proving Sample's two Hosts take the same step in its copied-sample consumer ([packages.md](packages.md)).
