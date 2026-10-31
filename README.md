![PouryaKit](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/kit.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Templates?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Templates)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices.

[Docs](https://pouryakit.vercel.app) · [Install](https://pouryakit.vercel.app/docs/get-started/install-templates)

All the Kit's packages are on nuget.org as previews, and the [docs site](https://pouryakit.vercel.app) shows how to use them. The packages (every entry of `scripts/packaging/inventory.json`), each usable on its own, make up these Capabilities, plus two features (Paging, Request idempotency) that span several of them:

- [Domain primitives](.agents/skills/pourya-kit/references/domain-primitives.md) (`PouryaKit.Domain`)
- [Commands, queries and validation](.agents/skills/pourya-kit/references/commands-queries-validation.md) (`PouryaKit.Cqrs`, `PouryaKit.Validation`)
- [Modules](.agents/skills/pourya-kit/references/modules.md) (`PouryaKit.Modularity`, with the Host's composition checks in `PouryaKit.Cqrs`)
- [Persistence and the Unit of Work](.agents/skills/pourya-kit/references/persistence.md) (`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`)
- [Dapper reads](.agents/skills/pourya-kit/references/dapper-reads.md) (`PouryaKit.Persistence.Dapper`)
- [Messaging, delayed and recurring commands included](.agents/skills/pourya-kit/references/messaging.md) (`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`)
- [Web](.agents/skills/pourya-kit/references/web.md) (`PouryaKit.Web`)
- [Testing](.agents/skills/pourya-kit/references/testing.md) (`PouryaKit.Testing`)
- [Core helpers](.agents/skills/pourya-kit/references/core.md) (`PouryaKit.Core`)
- [Security](.agents/skills/pourya-kit/references/security.md) (`PouryaKit.Security`, `PouryaKit.Security.JwtBearer`)
- [Observability](.agents/skills/pourya-kit/references/observability.md) (`PouryaKit.Observability`)
- [Caching](.agents/skills/pourya-kit/references/caching.md) (`PouryaKit.Caching`)
- [Paging](.agents/skills/pourya-kit/references/commands-queries-validation.md#paging) (`PouryaKit.Cqrs`, `PouryaKit.Persistence.Dapper`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Web`)
- [Request idempotency](.agents/skills/pourya-kit/references/commands-queries-validation.md#request-idempotency) (`PouryaKit.Cqrs`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Web`)
- [API versions and OpenAPI](.agents/skills/pourya-kit/references/openapi.md) (`PouryaKit.Web.OpenApi`)
- [Project templates](.agents/skills/pourya-kit/references/templates.md) (`PouryaKit.Templates`: the monolith, modular monolith and service templates)
- [RabbitMQ between Hosts](.agents/skills/pourya-kit/references/rabbitmq.md) (`PouryaKit.Messaging.Wolverine.RabbitMq`)
- [Email](.agents/skills/pourya-kit/references/email.md) (`PouryaKit.Email`, `PouryaKit.Email.MailKit`)
- [Multi-tenancy](.agents/skills/pourya-kit/references/multi-tenancy.md) (`PouryaKit.Domain`, `PouryaKit.Cqrs`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Messaging.Wolverine`: tenant-scoped Modules and the checks that keep one tenant's data from another)
- [File storage](.agents/skills/pourya-kit/references/storage.md) (`PouryaKit.Storage`, `PouryaKit.Storage.S3`, `PouryaKit.Storage.AzureBlob`: the contract and its S3 and Azure Blob providers)
- [Localization](.agents/skills/pourya-kit/references/localization.md) (`PouryaKit.Localization`; optional: English and Persian)
- [Reverse proxy](.agents/skills/pourya-kit/references/reverse-proxy.md) (`PouryaKit.ReverseProxy`: a YARP Host of its own process in front of the Hosts)
- [gRPC](.agents/skills/pourya-kit/references/grpc.md) (`PouryaKit.Grpc`: services and clients on Web's conventions)
- [Aspire resources](.agents/skills/pourya-kit/references/aspire.md) (`PouryaKit.Aspire`; AppHost-only: PostgreSQL, Redis, RabbitMQ and Ollama on pinned images, and the Proving Sample's AppHost)
- [Realtime](.agents/skills/pourya-kit/references/realtime.md) (`PouryaKit.Realtime`; optional: server-sent events from Notification Handlers, heard on every instance through PostgreSQL's notifications)
- [Reporting](.agents/skills/pourya-kit/references/reporting.md) (`PouryaKit.Reporting`; optional: a Module's report layout rendered to PDF, CSV and a spreadsheet)
- [Report Module](.agents/skills/pourya-kit/references/reporting-module.md) (`PouryaKit.Reporting.Module`; optional: the first Module the Kit ships as a package, report jobs generated in the background, fenced, served to their requester and removed after their retention)
- [AI](.agents/skills/pourya-kit/references/ai.md) (`PouryaKit.AI`, `PouryaKit.AI.Postgres`: the chat client, the Gateway tool, MCP clients, agents and workflows, embeddings and semantic search)
- [Streams](.agents/skills/pourya-kit/references/stream.md) (`PouryaKit.Persistence.Marten`, `PouryaKit.Messaging.Wolverine.Marten`: the Stream write model on Marten, committed with its Notifications and Integration Events through Wolverine's Marten outbox)
- [Sagas](.agents/skills/pourya-kit/references/sagas.md) (`PouryaKit.Messaging.Wolverine.Sagas`; optional: a Module's Saga on Wolverine, routed, claimed, tenant-guarded and emitting through the Kit, outside every Operation)

The skill's [Capability table](.agents/skills/pourya-kit/SKILL.md#capabilities) lists them with their packages and says when to read each reference.

The Proving Sample in `samples/ModularMonolith/` proves every guarantee against PostgreSQL, and against RabbitMQ and Redis where they apply, and architecture tests guard its boundaries and every package's dependencies. Its `ModularMonolith.Host` composes four Modules, the EF Core Modules Catalog, Inventory and Billing and the Stream Module Ledger, or all but Inventory when `Inventory.Host` receives from it over RabbitMQ, and the Kit's [report Module](.agents/skills/pourya-kit/references/reporting-module.md) with one report kind of its own. `Inventory.Host` and `ModularMonolith.ReverseProxy`, the [Reverse proxy](.agents/skills/pourya-kit/references/reverse-proxy.md) in front of the Hosts, compose the optional [service defaults recipes](.agents/skills/pourya-kit/references/host-recipes.md) of `ModularMonolith.ServiceDefaults` (OpenTelemetry export, liveness and readiness, service discovery, one HTTP resilience pipeline, Serilog), which are template content, not a Kit package.

Build and test with the .NET SDK pinned in `global.json`:

```bash
dotnet build PouryaKit.slnx
dotnet test PouryaKit.slnx
```

[Package validation](docs/packages.md) checks the Release package set, the PostgreSQL suite and clean consumers. [CI](docs/ci.md) runs it, and [private packages](docs/private-packages.md) covers publication and consumption.

- Using the Kit, Capability by Capability: the Kit's skill, [.agents/skills/pourya-kit/](.agents/skills/pourya-kit/SKILL.md)
- Packages, their references and the Unit of Work's commit sequence: [docs/architecture/README.md](docs/architecture/README.md)
- Diagrams of the Kit's structure, runtime flows and publication, in reading order: [the architecture document's Diagrams section](docs/architecture/README.md#diagrams)
- Which test proves each acceptance scenario: [docs/acceptance.md](docs/acceptance.md)
- Decisions: [docs/adr/](docs/adr/)
- Vocabulary: [GLOSSARY.md](GLOSSARY.md)
