# Project templates

`PouryaKit.Templates`, a `dotnet new` template package: a new application starts from the Proving Sample's business code in one of two compositions. The templates are generated from the sample, never maintained beside it.

- [Install and generate](#install-and-generate)
- [What each composition composes](#what-each-composition-composes)
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
| Business code | Catalog and `Catalog.Contracts` | Catalog, `Catalog.Contracts`, Billing and Inventory |
| Composition Root | `CatalogModule`, a static class whose `services.AddCatalog()` is a plain registration on the Host's services | Each Module's `IModuleDefinition`, added with `AddPouryaKitModules` |
| Endpoint adapters | Each Feature's endpoint sends through `IDispatcher` | Each Feature's endpoint sends through `IGatewayProcessor<CatalogModule>` |
| Kit packages | Every Wave 2 Capability that applies, without `PouryaKit.Modularity` | Every Wave 2 Capability, with `PouryaKit.Modularity` |
| Start checks | Container validation and the composition checks from `AddPouryaKitCompositionChecks` | Both from `AddPouryaKitModules`, which calls it |

- **The same business code.** Aggregates, commands, queries, handlers, validators, the repository, Dapper reads and every event handler are the sample's own files in both. Only the Composition Root and the endpoint adapters differ, and in the modular monolith they are the sample's own too.
- **The monolith is one Module in all but name** (plan I.1). A persistence package adds its `IUnitOfWork` with `Add`, and only Modularity's `ModuleBuilder` keys each Module's own, so a Host without Modularity holds one Unit of Work: one Module's data. The monolith therefore takes Catalog alone; Billing and Inventory stay in the modular monolith. Its Operation names no Module, its Unit of Work is the Host's, and Catalog publishes with no Module, so its Integration Events still reach another Host over RabbitMQ when a broker is configured.
- **The Host composition**, in `Program.cs`, is the same in both: JWT bearer authentication from `Authentication:Schemes:Bearer`, authenticated-by-default HTTP, trusted peers, CORS, compression and the rate limit per Actor, the API versions and OpenAPI, idempotency key retention, the `downstream` HTTP client with the Operation Context, permissions from the caller's `permission` claims (`CallerPermissions`), Observability, Caching with Redis when `ConnectionStrings:cache` is set, Wolverine over EF Core on `ConnectionStrings:app`, RabbitMQ when `ConnectionStrings:broker` is set, and the service defaults recipes ([host-recipes.md](host-recipes.md)): `AddServiceDefaults()`, `AddSerilogDefaults()` and `MapDefaultEndpoints()`. The modular monolith's Inventory reconciles stock at `Inventory:ReconcileStock:Every`.
- **Calls between Modules stay in-process**: Integration Events and Snapshots through the Host's Outbox, a question through the owning Module's Contracts. Never HTTP between Modules of one Host.
- **The same start checks in both.** The monolith calls Cqrs's Host entry, the one `AddPouryaKitModules` calls, so a declared permission without its check, a cacheable query without caching or a command without a Unit of Work stops the Host before it serves, and its container is validated in every environment ([modules.md](modules.md#startup-checks)):

```csharp
builder.AddPouryaKitCompositionChecks();
```

- **The monolith names its assembly; the modular monolith infers it.** The monolith's `AddCatalog()` runs on the Host's own services, which are no Module's builder, so its registrations name `typeof(CatalogModule).Assembly` and its declarations belong to the application. Each modular Composition Root registers on its Module's builder with no assembly and no Module ([modules.md](modules.md#the-composition-root)).

## Running the generated Host

```bash
cd Acme.Shop
dotnet tool restore
# ConnectionStrings:app in src/Acme.Shop.Host/appsettings.json names the PostgreSQL database.
dotnet ef database update --project src/Modules/Catalog/Catalog --startup-project src/Acme.Shop.Host --context CatalogDbContext
# The modular monolith also migrates Billing (BillingDbContext) and Inventory (InventoryDbContext) the same way.
dotnet user-jwts create --project src/Acme.Shop.Host
dotnet run --project src/Acme.Shop.Host
```

- The Host applies no migrations at startup; they are the Host's, as [persistence.md](persistence.md) says.
- Without `Authentication:Schemes:Bearer` the Host does not start. `dotnet user-jwts create` fills it for development and prints a token; in production, configure the issuer, audiences and signing keys of the real identity provider.
- The `http` launch profile runs in Development on `http://localhost:5080`, where `/health`, `/alive`, the OpenAPI documents and the Scalar reference are mapped. A token's `sub` is the Actor, its `tenant` the tenant, and its `permission` claims the Actor's permissions.

## The smoke test

`tests/Acme.Shop.Tests` hosts the generated `Program` with `WebApplicationFactory<Program>` over PostgreSQL in Testcontainers (Docker must run), applies every DbContext's migrations in a hosted service the Host starts before Wolverine, and calls it with bearer tokens of a test issuer configured through `Authentication:Schemes:Bearer`. It does not use `PouryaKit.Testing`, which references Modularity.

- A category and a product created over HTTP, the product with an `Idempotency-Key`: 201 with its id and versioned location; the same key again answers the same status, body and location, and the filtered, sorted list still has one product.
- `Program` hands its command line to JasperFx, which reads the factory's settings as no command it knows, so the fixture sets `JasperFxEnvironment.AutoStartHost`: JasperFx then starts the Host first (Wolverine's integration testing guide).
- `/alive` answers 200 through the service defaults. That it asks nothing outside the process, so it stays healthy while the database is down, is the recipe's own proof (`HostRecipeTests` in the Proving Sample, #52).
- Composed with a `ConnectionStrings:broker` value, the Host joins the broker under its derived service name (below). The test builds it with no hosted service running, so no broker is needed.
- With the permission checks removed from the registrations, the Host does not build: `RemoveProductsCommand declares RequiresPermissionAttribute, but its check is not registered`.
- The modular monolith adds one cross-Module effect: a product Catalog creates opens its stock item in Inventory's schema.

```bash
dotnet test --project tests/Acme.Shop.Tests/Acme.Shop.Tests.csproj
```

## The optional recipes

- `src/Acme.Shop.ServiceDefaults` is the application's own copy of the service defaults recipes: OpenTelemetry with OTLP export when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, liveness and readiness, service discovery, one resilience pipeline for every `HttpClient`, and Serilog. Change or drop any of them there ([host-recipes.md](host-recipes.md)).
- The `downstream` client takes its resilience from the defaults and adds only `AddPouryaKitOperationContext()`. A client that needs other limits removes the default pipeline first, as [host-recipes.md](host-recipes.md#one-resilience-pipeline) shows; adding `AddPouryaKitResilience()` on top would stack a second pipeline, which the Kit refuses when the client is created.
- `/alive` asks nothing outside the process; `/health` also asks each database.
- Redis (`ConnectionStrings:cache`) and RabbitMQ (`ConnectionStrings:broker`) are off until configured. With a broker, the Host's queue, its service name on the broker, is the project's name lower-cased, each run of characters other than letters and digits a hyphen: `-n Acme.Shop` gives `acme-shop`. The template's `serviceName` symbol derives it, because a queue name takes only letters and digits in segments joined by single dots or hyphens, and the Host refuses any other name as it is composed (`UsePouryaKitRabbitMq`). A name with no letter or digit gives none, and another kind of Host on the same broker needs a name of its own; another Host that receives Catalog's events declares them as [rabbitmq.md](rabbitmq.md#a-host-that-receives) shows.
- CORS policies (`PouryaKit:Web:Cors`), compression (`PouryaKit:Web:Compression`), the rate limit (`PouryaKit:Web:RateLimit`), trusted peers (`PouryaKit:TrustedPeers`) and idempotency key retention (`PouryaKit:Idempotency`) are read from configuration; none is set.

## Pre-generating Wolverine's code for production

Outside Development, Wolverine loads its generated handler code from the Host's assembly instead of compiling it when the Host starts: `UsePouryaKitWolverine` sets `TypeLoadMode.Static` there ([messaging.md](messaging.md#declaring-handlers-and-running-deliveries)). The generated Host follows Wolverine's code generation guide (`JasperFx/wolverine`, `docs/guide/codegen.md`) and ADR 0004:

- `Program.cs` ends with `return await app.RunJasperFxCommands(args);`, so the Host's command line is JasperFx's: `dotnet run` runs the Host, `dotnet run -- codegen write` writes Wolverine's generated code into `src/Acme.Shop.Host/Internal/Generated`, `codegen preview` prints it, `check-env` runs the environment checks and `help` lists the rest. Configure the Host through settings files, user secrets or the environment: an argument that is no JasperFx command, such as `--ConnectionStrings:app=...`, stops it with "Invalid usage".
- The Host references `WolverineFx.RuntimeCompilation` in Debug builds only (`<ItemGroup Condition="'$(Configuration)' == 'Debug'">`): Development compiles at run time, and a Release publish carries neither `Wolverine.RuntimeCompilation.dll` nor `JasperFx.RuntimeCompiler.dll`. Roslyn itself stays in the publish through `Microsoft.EntityFrameworkCore.Design`, which the EF Core outbox brings. The test project references the package itself, since it hosts `Program` in Development from any build.
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

- `src/PouryaKit.Templates` packs the templates' own files, the Hosts, project files, solution, central versions and smoke test, and packs the business code from `samples/ModularMonolith/src` when the package is built, so no copy of a sample file is kept. The package's version, the Kit's MinVer version, is written into each template's `Directory.Packages.props` as it is packed.
- The `sourceName` is the sample's own `ModularMonolith`, a valid identifier, so its form as typed and its namespace-safe form are one string, and the engine writes the namespace-safe form of `-n` everywhere in content while it renames files and folders with the name as typed. The template's own solution and project files therefore name each project's path through the token `PouryaKitAppName`, which the `projectName` symbol (`derived` from `name` with the `identity` form) replaces with the name as typed, as Aspire's starter template gets from a source name whose two forms differ. The packed sample files keep `ModularMonolith` for their namespaces.
- The monolith's `template.json` excludes the sample's `*Module.cs`, `*Endpoint.cs` and `CatalogProducts.cs` and supplies its own Composition Root and endpoints at the same paths. A new Gateway endpoint in the sample fails `TemplateTests` until the monolith has its own.
- `TemplateTests` (`tests/PouryaKit.ArchitectureTests`) asserts that the template content holds no copy of a sample file, that nothing the monolith generates names Modularity, that each excluded file is replaced, that each template project references what the sample's project at its place does, that the central versions are the Kit's, that each Host passes the derived `serviceName`, and that each solution and project file names its projects' paths through `projectName`'s token, never the `sourceName`, and names only projects the template generates; each symbol is the same in both templates.
- `scripts/packaging/validate.py` installs the produced package into a hive of its own, generates both templates, the monolith with `-n Acme.Shop` and the modular monolith with `-n acme-shop`, restores and builds them from the produced packages only, refuses `PouryaKit.Modularity` in the monolith's closure and `PouryaKit.Testing` in both, runs each generated smoke test, then runs `codegen write` on each generated Host and checks its Release publish: the Host built with the generated code, without the runtime compiler. The Proving Sample's two Hosts take the same step in its copied-sample consumer ([packages.md](packages.md)).
