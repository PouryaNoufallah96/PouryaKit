# Foundation kit: independent review and plan

Status: draft 6, 2026-10-04 (the owner's decisions applied). The kit is named **PouryaKit**. Every API shown under the `PouryaKit.*` prefix is
**proposed**, not existing CloudNativeKit/Woo functionality. *Phase 0 and Waves 1 to 4 have since been built from this plan. Where the build departed from it, a dated note in
place or an amendment in `docs/adr/` says so; `docs/acceptance.md` maps the acceptance scenarios to tests, and the API as
built is in the Kit's skill.*

*Current sections:* section C's [Allowed references](#allowed-references) table, E's [Provenance and
licensing](#provenance-and-licensing), which adoption work follows, G, and I.3, I.6 and I.7. History, the record of what was
reviewed and planned: A, B, D, the rest of E and the [plan in one paragraph](#recommended-plan-in-one-paragraph); where one
differs from a current section, the current one holds. No section moves, since code and docs cite them by letter.

## Inspected snapshots

| Repo | Commit | Branch | Worktree |
|---|---|---|---|
| Cybernod | `bae8ac3` | `chore/local-app-changes` | dirty: `global.json` (SDK 10.0.103 → 10.0.401), MCP config. SDK claims use the committed value. |
| CloudNativeKit (CNK) | `e81d65e` | `main` | clean |
| Woo | `149a3cf` | `main` | clean |
| food-delivery-microservices | `c83bca3` | `main` | `.DS_Store` only; CNK submodule dir empty, pinned at `f995102` |
| modular-monolith-with-ddd | `91c8ef2` | `master` | `.DS_Store` only |
| vertical-slice-api-template | `4a39807` | `main` | clean |
| ultrabusiness_server_microservices | `a85eacd` | `pourya` | clean |

Package facts were checked against nuget.org and official docs on 2026-10-04.

---

## A. Verdict

**Own the foundation. Do not compose CNK or Woo packages.** The proposal's central decision is right,
and the evidence for it is stronger than the proposal states:

- Neither kit is published (nuget.org returns 404 for both id families), so "compose packages" is
  not actually available.
- In both kits the transaction and outbox paths are broken in source, and neither has a single test
  that exercises them (CNK: 22 integration projects of `Assert.True(true)`; Woo: zero test projects).
- CNK's own flagship consumer (food-delivery) gets no outbox at all: the kit ignores the caller's
  `EnableDurability = true`
  (`CloudNativeKit.Integration.Wolverine/Extensions/DependencyInjectionExtensions.cs:35-36,102-105`).

What nobody has, including Cybernod and Ultrabusiness: a transaction owner that rolls back on a
failed `Result`, drains chained events to a fixed point, and retries on a fresh scope. That piece is
**new work assembled from known patterns**, not an extraction.

### Where I agree with the previous proposal

- Owned, capability-organised packages; Cybernod-shaped typed IDs, aggregates, `Result`, commands.
- One commit owner; handlers never save; failed `Result` rolls back; sequential drain with a cycle
  guard; no whole-handler retry on a used DbContext.
- EF Core + Npgsql for writes, Dapper for reads; one DbContext, schema and migration history per
  module, in one physical database.
- Native Wolverine with PostgreSQL durability and durable local queues; no broker by default.
- A business receipt `(consumer, event_id)` committed in the receiving module's transaction.
  Wolverine's own inbox dedup is keyed on envelope id and keeps handled envelopes 5 minutes by
  default, so it does not replace this.
- `Contracts` + implementation project per module; proving sample first; publish after proof.

### Where I would change it

1. **Drop MediatR from the kit, as a dependency policy.** MediatR ≥ 13 is offered under RPL-1.5 or
   the Lucky Penny commercial agreement; 12.5.0 is the last Apache release and is frozen. Under the
   commercial agreement, §4.2.e.3 restricts using it to create or distribute "frameworks, controls,
   wrappers, abstractions … intended to be redistributed … as standalone development tools or
   libraries" (https://luckypennysoftware.com/license); under RPL, deployed extensions carry
   source-release duties. Two qualifications: `IRequest<T>` itself lives in `MediatR.Contracts`
   (Apache-2.0, excluded from the commercial product by §1.3.3), and the RPL route is governed by
   RPL, not that agreement — so inheriting `IRequest` alone is not what creates the problem. The
   kit's pipeline, behaviours and registration would need the main `MediatR` package, and that is
   where the constraint bites. This is my reading of the text, not a legal determination. The
   policy I recommend: **the kit takes no runtime dependency on a reciprocal or commercially
   licensed package**, so every consumer can use it without a licence question. Cybernod (14.0.0)
   and Ultrabusiness (14.2.0) keep their own keys as applications. Same policy for AutoMapper ≥ 15
   and MassTransit 9.
2. **One host container, not a private `ServiceProvider` per module.** See "The fork" below. This
   removes the `Foundation.Modularity.Wolverine.EFCore` bridge package and most of its custom code.
3. **Cut first scope from ~22 packages to 12.** No `ServiceDefaults`, `Security`, `Security.JwtBearer`,
   `Caching*`, `Observability`, `Logging.Serilog`, `Resilience`, `HealthChecks`, `Aspire`,
   `Persistence.Abstractions` packages in v1. Each is either a wrapper over something .NET/Aspire
   already ships, or has no second implementation to justify an abstraction package.
4. **Keep Cybernod's handler ergonomics.** The proposal's `USAGE.md` writes
   `Result.Failure<ProductDto>(created.Errors)` and `Result.Success(ProductDto.From(x))` everywhere,
   and makes every command carry a `CommandId` with a `NotEmpty` rule. Cybernod handlers write
   `return error;` / `return value;` (`AddNucleiScanCommandHandler.cs:62-73`). Keep the implicit
   `T → Result<T>` and `Error → Result<T>` conversions; remove only the unsafe ones (`Result<T> → T`,
   and null → success at `BuildingBlocks.Core/…/Result.cs:139`). Drop `CommandId` from the base until
   request idempotency is actually built — nothing in the proposal consumes it.
5. **Add Ultrabusiness as a source.** The proposal covered six repos. Ultrabusiness has the only
   retry-safe transaction step and the only real outbox tests found anywhere (section B).
6. **Spike before owning the custom fan-out.** The proposal expands each event into a
   `ModuleDelivery` per `(module, consumer)` with its own type registry. Wolverine documents
   `MultipleHandlerBehavior.Separated` and sticky handlers for independent per-handler queues. That
   may make the expansion unnecessary. Unverified at runtime either way.

### The fork: private module containers vs one container

Wolverine supports only the built-in `ServiceProvider`; `IDbContextOutbox<T>`, its EF integration and
handler discovery all resolve from that one container
(https://wolverinefx.net/guide/runtime.html). The proposal keeps a private provider per module, so
it must own a bridge that re-enters module providers and hand-wires the outbox there.

| | (a) One container — **recommended** | (b) Private provider per module (proposal) |
|---|---|---|
| Isolation mechanism | Separate assemblies, `internal` types, per-module DbContext/schema, architecture tests, gateway-opened operation scope tagged with the module | Separate `ServiceProvider` per module |
| Wolverine outbox | `IDbContextOutbox<TModuleDbContext>` resolved natively | Manually constructed inside each provider; unverified against Wolverine 6.45 |
| Startup validation | One `ValidateOnBuild`/`ValidateScopes` for the whole app | Per-provider validation to build |
| Shared singletons | One instance | Must be passed in explicitly or they duplicate (Cybernod duplicates AI/Blob/Quartz/cache per module: `ModuleServiceRegistration.cs:116-153`) |
| Owned code | Gateway, scope tag, pipeline | All of (a) plus provider lifecycle, shared-instance plumbing, delivery re-entry bridge |
| What you lose | A module could resolve another module's *public* DI service. `internal` already blocks the rest at compile time. | — |

Your stated requirement is "module and operation scopes", gateways and composition roots — (a)
delivers all three. Private containers were Cybernod's implementation, and its snapshot-based version
is the most fragile part of that codebase (root registrations after the snapshot are invisible,
cross-module messaging depends on `static` fields: `ModuleServiceCollectionExtensions.cs:20-27`,
`InMemoryEventChannelBus.cs:15-16`). `IGatewayProcessor<TModule>` stays as the public entry point in
both designs, so feature code is identical. **Decided: (a).**

A third option — make every handler a native Wolverine handler with `AutoApplyTransactions` — is
rejected: Wolverine has no `Result`-aware rollback, publishes EF domain events as post-commit
messages rather than same-transaction reactions, and supports no open-generic handlers. It would
replace your conventions with Wolverine's.

**Dispatch.** With MediatR gone, own a small dispatcher (closed-generic handler lookup from DI plus a
fixed pipeline; a few hundred lines with tests). The pipeline is fixed by the kit, so nothing needs a
pluggable behaviour model. martinothamar `Mediator` 3.x (MIT, source-generated) is the alternative;
its generator runs in the host project and whether it can see `internal` handlers in module
assemblies is not verified. Phase 0 checks it; default is the owned dispatcher. *Resolved by Spike S3
(ADR 0002): it can, but only with `InternalsVisibleTo` from each module to the host or a generator and
generated mediator inside every module; the owned dispatcher stands.*

---

## B. Evidence and adoption matrix

### B.1 The 17 behaviours, per candidate

N = native library guarantee, W = repo wrapper, — = missing. ✗ marks a defect.

| # | Behaviour | Cybernod | CNK (+food-delivery) | Woo | mm-ddd | Ultrabusiness |
|---|---|---|---|---|---|---|
| 1 | Commit owner | W `TransactionBehavior` + commit-time save; 23/46 handlers also save | W split: behaviour commits, handler saves; never saves itself ✗ | W `TransactionScope`; completes only if events exist ✗ | W UoW decorator, sole commit ✓ | W `TransactionBehavior`; handler save is a flush |
| 2 | Failed Result rolls back | ✗ commits (`TransactionBehavior.cs:62-70`) | no Result type; returned failure commits | no Result type | exceptions only | ✗ commits (`TransactionBehavior.cs:70-78`), documented workaround |
| 3 | No-event command saves | ✓ | only if handler saved | ✗ (`EfTxBehavior.cs:55-58`) | ✓ | ✓ |
| 4 | Domain handlers run | pre-commit inside `SaveChanges` interceptor; + post-commit notifications from outbox | pre-commit, own saves | none in-process | pre-save, same DbContext ✓; notifications post-commit | all post-commit from outbox |
| 5 | Chained events drained | partial; events cleared before publish, no loop | — | — | W re-dispatch after each handler, no depth guard | next poll, no limit |
| 6 | Shared-DbContext handlers safe | sequential, but `RetryAsync(2)` reruns all | `Task.WhenAll` in `MessagePublisher` ✗ | n/a (Mongo `WhenAll` ✗) | publisher not pinned (unverified) | scope per message; batch `Task.WhenAll` loses order |
| 7 | Outbox atomic with data | ✓ EF path; ✗ Dapper writes | ✗ sends before commit; durability off | ✗ sends before save | ✓ same `SaveChanges` | ✓ domain outbox, **tested**; bus outbox only via decorator side effect ✗ |
| 8 | Receipt commits with effect | ✗ separate; internal-command receipt written *before* commit | — | — | inbox ✗; internal command ✓ | ✗ marker after effect; no consumer inbox |
| 9 | Duplicate / concurrent delivery | ids regenerate, dedupe void after crash | no usable message id | — | duplicate poisons outbox forever ✗ | sequential redelivery suppressed, **tested**; concurrent untested |
| 10 | After commit, before ack | static in-memory channel between outbox and inbox loses events ✗ | ghost messages | ghost messages | at-least-once or stuck | re-poll + marker, **tested**; dead-letter at 5 |
| 11 | Independent subscribers | per-handler receipt in-module ✓; cross-module sequential, first failure drops ✗ | shared default queue = competing | shared queue | one ack flag for all ✗ | per-handler marker ✓; per-service queues (N) |
| 12 | Nested commands | join tx; failed Result ignored | join; drains outer events early ✗ | silent outer rollback ✗ | separate tx each | join; needs `SendOrThrowAsync` |
| 13 | Retry on mutated scope | ✗ EF strategy wraps handler | ✗ | retry is a no-op | ✓ fresh scope per attempt | ✓ `ChangeTracker.Clear` on rollback; outbox fresh scope |
| 14 | Stable ids / timestamps | domain ✓; integration + command ✗ | `MessageId` new per read ✗ | `EventId` new per read ✗ | regenerated on outbox read ✗ | domain ✓; integration unverified |
| 15 | Dapper lifetimes explicit | explicit, never joins EF tx | leaks, separate connection | no Dapper | no transactions anywhere | shared scoped connection, never joins |
| 16 | Background context | — (HttpContext only) | correlation only | — | — (throws) | opt-in `AsyncLocal`; user/trace lost in outbox |
| 17 | Package boundaries hold | ✗ | ✗ | ✗ | ✗ | ✗ |

No candidate is adoptable as the transaction/delivery owner. Row 17 fails everywhere for the same
reason: a `Core` project that everything references.

### B.2 Component adoption

| Capability | Start from | Action | Evidence | Required change / proof |
|---|---|---|---|---|
| Typed IDs + EF/Dapper mapping | Cybernod | **Adapt** | `TypedIdValueBase.cs:8-33`, `StronglyTypedIdValueConverterSelector.cs:18-81`, `TypedIdValueHandler.cs:11-25` | Getter-only value; static factory instead of reflection; register converters via `ConfigureConventions` rather than replacing an EF internal service; Dapper handlers move to the Dapper package |
| Entity / AggregateRoot / business rules | Cybernod; `Version` token from Ultrabusiness | **Adapt** | `AggregateRoot.cs:10-60`; UB `AggregateRoot.cs:16,34-40` | Remove MediatR base from events; id equality on `Entity<T>`; plain list not `ConcurrentQueue`; `TimeProvider`. **One owner advances `Version`:** the unit of work bumps it once per commit for every aggregate with any change, with or without events (Ultrabusiness splits this across `AddDomainEvent` and `AuditableEntityInterceptor.cs:65-70`; Cybernod's interceptor only stamps dates). Domain events are facts and carry no version. Exported **state snapshots** are produced by the kit in a finalization step after the drain reaches its fixed point: bump each changed aggregate once, then build one snapshot per aggregate per contract from final state and the committed version |
| Result / Error | Cybernod | **Adapt** | `Result.cs:10-204`, `AppError.cs:17-229` | `ErrorKind` instead of HTTP status as code; keep safe implicit conversions; return all errors |
| Domain event base | Cybernod | **Adapt** | `DomainEvent.cs:24-28` | Id + `OccurredOn` assigned once, `init`, UTC; no default-interface members (`IEvent.cs:15,20`) |
| Command/query contracts | Cybernod shape | **Adapt** | `ICommand.cs:10`, `CommandHandler.cs` | Detach from MediatR; drop cache/header flags from `Command<T>` |
| Dispatcher + pipeline | — | **New (small)** | dependency policy on MediatR; Wolverine has no open generics | Fixed order: logging → authorization → validation → unit of work. Failure tracking sits in the dispatcher, outside every short-circuiting step: any nested command that fails — including on authorization or validation — marks the active operation rollback-only |
| Unit of work | mm-ddd order + Ultrabusiness retry safety | **New, assembled** | mm-ddd `UnitOfWork.cs:19-26`, `UnitOfWorkCommandHandlerDecorator.cs:25-43`; UB `TransactionBehavior.cs:45-86`, `BaseEfDbContext.cs:82-95` | Result inspection; drain loop with depth guard; honours the dispatcher's rollback-only flag; no EF retrying strategy around the handler |
| Validation | Cybernod behaviour shape | **Adapt** | `ValidationBehavior.cs:49-54` (throws) | Return `Result` with all failures; scan the module assembly only |
| Module definition, gateway | Cybernod API shape | **Adapt API, replace internals** | `GatewayProcessor.cs:24-40`, `ModuleExtensions.cs:143-157` | No collection snapshot, no static registries; scope opened in the host container |
| EF base, audit, soft delete | Cybernod / CNK fragments | **Adapt** | CNK `EfDbContextBase.cs:44-71`; Cybernod `AuditableEntityInterceptor` | Audit stamping and soft-delete conversion run as an explicit *prepare* step of the unit of work, before snapshots are built, not as `SaveChanges` interceptors that change state afterwards (CNK's `SoftDeleteInterceptor.cs:30-35` flips a shadow flag only during save; its interceptors are also never attached: `…EfCore.Postgres/…/DependencyInjectionExtensions.cs:56-59,88`). Deletion state is a real property the mapper can read. No event dispatch inside interceptors |
| Postgres registration, schema-per-module | Cybernod | **Adapt** | `EfCorePostgresExtensions.cs:34-58,98-111` | Remove global `EnableLegacyTimestampBehavior`, pgvector, `EnableRetryOnFailure` |
| Dapper reads | Cybernod | **Adapt** | `BuildingBlocks.Persistence.EfCore.Postgres/DbConnectionFactory.cs:19-31` already returns a fresh caller-owned connection from `NpgsqlDataSource` (CNK, mm-ddd and Ultrabusiness cache one per scope) | Move into the Dapper package without the EF reference; add an explicit way for a module-internal Dapper write to join the unit-of-work transaction, which Cybernod lacks. **Dapper writes are limited to non-aggregate tables** (read models, projections); aggregates change only through the tracked EF path, otherwise versioning and snapshots are bypassed |
| Durable messaging | WolverineFx 6.45 (MIT) | **Use package** | native EF outbox in the same `SaveChangesAsync`, durable local queues, PostgreSQL store | Wire correctly: publish through the enrolled `IDbContextOutbox<T>` (CNK enrolls one object and sends through another: `WolverineMessagePersistenceService.cs:30-32,122-143`) |
| Wolverine policy block | Woo | **Extract** | `Wolverine/Extensions.cs:38-60,109-142` | Remove RabbitMQ; explicit queue naming |
| Receiving receipt | proposal design; mm-ddd internal-command idea | **New** | no candidate commits receipt with effect | Unique `(consumer, event_id)` insert on the module transaction |
| Background context | Ultrabusiness idea | **New** | UB `InternalRequestContext.cs:13-26` | Immutable operation context carried in message headers; never read `HttpContext` in handlers |
| Result → HTTP, ProblemDetails | Cybernod + vertical-slice | **Adapt** | `EndpointResponseHandler.cs:80-95`; VSAT `ProblemDetailsService.cs:10-108` | All errors, RFC 9457 `errors`; never echo exception messages |
| Endpoint convention | Cybernod `IEndpointDefinition` | **Adapt** | `EndpointExtensions.cs:24-37` | Per-module mapping hook that actually maps; route group + authorization per module |
| Versioning / OpenAPI | Woo, Cybernod | **Extract later** | Woo `ApiVersioningExtensions.cs:8-53`; Cybernod `OpenApi/*` | Optional package after v1 |
| Test support | Cybernod fixtures, Woo/CNK tracked-session helpers | **Adapt** | Cybernod `SharedFixture.cs:590-628`; Woo `TestBase.cs:205-318` | Assertions must fail when nothing was sent (food-delivery's helper passes on zero: `SharedFixture.cs:399-411`) |
| Acceptance scenarios | Ultrabusiness Treasury tests | **Port scenarios, not files** | `IncomingChequeReceiptCreatedForChequeDomainEventHandlerTests.cs:44,143,353,456` | Re-express against PostgreSQL + Wolverine |
| Architecture tests | Cybernod, mm-ddd | **Adapt** | Cybernod `ArchitectureTests/Tests.cs:11-67` | Add negative cases and package-graph assertions |
| Caching, OTel, resilience, health | .NET / Aspire | **Use native, no kit package** | HybridCache 10.10 GA; Aspire 13.6 ServiceDefaults template | Ship as template content |

---

## C. Package structure

### First scope (13 projects — see G.4 for `PouryaKit.Persistence`)

```text
pourya-kit/
├── PouryaKit.slnx  Directory.Build.props  Directory.Packages.props
├── src/
│   ├── PouryaKit.Domain/                 Result, Error, typed IDs, Entity, AggregateRoot,
│   │                               IDomainEvent, IBusinessRule
│   ├── PouryaKit.Cqrs/                   command/query/domain-handler contracts, dispatcher, fixed
│   │                               pipeline, IUnitOfWork, OperationContext, authorization hook
│   ├── PouryaKit.Validation/             FluentValidation step returning Result
│   ├── PouryaKit.Modularity/             IModuleDefinition, ModuleBuilder, IGatewayProcessor<TModule>,
│   │                               module-tagged scopes, startup validation
│   ├── PouryaKit.Persistence.EFCore/     EF unit of work, event collection, typed-ID conventions,
│   │                               audit/soft-delete interceptors, error mapping
│   ├── PouryaKit.Persistence.Postgres/   Npgsql registration, schema + migration history per module,
│   │                               unique-constraint → Error mapping
│   ├── PouryaKit.Persistence.Dapper/     read-connection lease, typed-ID type handlers
│   ├── PouryaKit.Messaging/              IIntegrationEventPublisher/Handler, wire-name registry, headers
│   ├── PouryaKit.Messaging.Wolverine/    runtime registration, policies, headers, delivery handler,
│   │                               failure → retry/dead-letter mapping (no EF, no database)
│   ├── PouryaKit.Messaging.Wolverine.EFCore/  outbox commit strategy, receipts, model mappings,
│   │                               PostgreSQL message store
│   ├── PouryaKit.Web/                    Result → HTTP, IExceptionHandler, endpoint convention,
│   │                               ClaimsPrincipal → OperationContext
│   └── PouryaKit.Testing/                Postgres fixture, reset, host factory, tracked-delivery helpers
├── tests/                          one test project per package + ArchitectureTests
└── samples/ModularMonolith/        Catalog (2 aggregates), Inventory, Billing (second subscriber)
```

Corrected on 2026-10-06 to match Wave 1 as built; the tree above keeps its original wording:

- `PouryaKit.Domain` has no `IDomainEvent`. A Domain Event derives from the abstract record `DomainEvent`, which
  assigns its `Id` and `OccurredOn` once.
- The unique-constraint → Error mapping, `MapPouryaKitUniqueConstraint`, lives in `PouryaKit.Persistence.EFCore`;
  `PouryaKit.Persistence.Postgres` only reads the violated constraint's name from PostgreSQL's error.
- The typed-ID conventions (G.3) store a typed ID as its value and never make a key database-generated: a numeric
  typed ID the database generates needs `ValueGeneratedOnAdd()` in its entity's own mapping.
- An aggregate-specific repository (G.4) derives from `EfRepository` and gets one protected `ListAsync<TResult>` that
  returns a loaded list, never an `IQueryable`.
- `tests/` follows three seams, not one project per package:
  `ModularMonolith.Tests` (Gateway seam, the Proving Sample against PostgreSQL), `PouryaKit.Domain.Tests` (Domain seam)
  and `PouryaKit.ArchitectureTests` (Structure seam), plus `PouryaKit.Cqrs.Tests` (with its test Module
  `PouryaKit.Cqrs.Tests.Module`) and `PouryaKit.Validation.Tests`, which run the dispatcher and the validation step,
  `PouryaKit.Core.Tests`, which proves Core's three helpers, and `PouryaKit.Security.Tests`, which proves
  the permission registration's checks, all without a database.
- `PouryaKit.Persistence` (G.4), the thirteenth project, has no entry in the tree above and no row in the table
  below. It holds `IRepository` and `IUnitOfWorkConnection`, the seam through which a Module's plain-SQL write joins
  the Unit of Work, and references Domain and `Ardalis.Specification`. `PouryaKit.Persistence.EFCore`
  references it beside Cqrs, with `Ardalis.Specification.EntityFrameworkCore`.

Corrected on 2026-10-10: the tree's `PouryaKit.Domain` line no longer names a value-object
base, which the package no longer has. Nothing derived from it, its one member repeated `Entity.CheckRule`, and a record
already gives value equality.

Recorded on 2026-10-10: the idempotency store, its cleanup and its table in `PouryaKit.Persistence.EFCore` are PostgreSQL-only, as the Receipts in `PouryaKit.Messaging.Wolverine.EFCore` are.

### Allowed references

| Package | References | Third-party |
|---|---|---|
| Domain | — | none |
| Core (I.3) | — | none; referenced by Cqrs, Web, Messaging.Wolverine, Persistence.Dapper, Persistence.EFCore and Security since 2026-10-07, whose registrations find their types through its `ImplementingTypes` |
| Cqrs | Domain, Core | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Logging.Abstractions`; `Microsoft.Extensions.DependencyInjection` and `Microsoft.Extensions.Hosting.Abstractions` (2026-10-07: the Host's validated container and its composition checks moved here from Modularity, so a Host without Modules installs them through Cqrs's `AddPouryaKitCompositionChecks`; MIT, under the standing rule below) |
| Validation | Cqrs | FluentValidation |
| Modularity | Cqrs | `Microsoft.Extensions.Hosting.Abstractions` (`Microsoft.Extensions.DependencyInjection` until 2026-10-07, which moved the validating factory that needed it to Cqrs) |
| Persistence.EFCore | Cqrs, Core | EF Core Relational; `Microsoft.Extensions.Hosting.Abstractions` (2026-10-07: the idempotency key cleanup is a `BackgroundService`; MIT, under the standing rule below. The composition check of each named connection string, `DbContextConnectionsCheck`, uses Cqrs's `ICompositionCheck` and `IConfiguration`, which this reference brings; no reference added) |
| Persistence.Postgres | Persistence.EFCore (2026-10-08: its composition check of each named connection string uses Cqrs's `ICompositionCheck` through it, and `IConfiguration` through EF Core; no reference added. The record of each DbContext's connection it registers, `DbContextConnection`, is Persistence.EFCore's, so Messaging.Wolverine.EFCore reads it too. That check moved to Persistence.EFCore, where the records are made) | Npgsql EF provider, EFCore.NamingConventions |
| Persistence.Dapper | Cqrs, Core (2026-10-07: Cqrs for the Page types; Domain comes through Cqrs) | Dapper, Npgsql (ADO.NET driver only, no EF), `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` |
| Messaging | Domain (handlers return `Result`) | none |
| Messaging.Wolverine | Cqrs, Messaging, Core | WolverineFx |
| Messaging.Wolverine.EFCore | Messaging.Wolverine, Persistence.EFCore (2026-10-08: its composition check of the store's database reads Persistence.EFCore's `DbContextConnection` and the connection EF Core gives each DbContext, unopened, through EF Core Relational, and parses connection strings with Npgsql's `NpgsqlConnectionStringBuilder`, which WolverineFx.Postgresql brings; no reference added) | WolverineFx.EntityFrameworkCore, WolverineFx.Postgresql |
| Messaging.Wolverine.RabbitMq (I.3) | Messaging.Wolverine | `WolverineFx.RabbitMQ` (MIT; it brings `RabbitMQ.Client`, `Apache-2.0 OR MPL-2.0` taken under Apache-2.0, and `WolverineFx.Newtonsoft` with `Newtonsoft.Json`, MIT); no EF Core, Npgsql or message-store package |
| Web | Cqrs, Core | ASP.NET Core framework reference (CORS, response compression and rate limiting come from it, 2026-10-07); `Microsoft.Extensions.Http.Resilience` (I.3: the standard outgoing retry, MIT; it brings Polly, BSD-3-Clause), no Aspire package |
| Security (I.3) | Cqrs, Core | none; no ASP.NET Core framework reference: the authenticated HTTP fallback policy is set by Web |
| Observability (I.3) | Cqrs (the plan listed Messaging too; nothing from it is used since the parent is recorded on the Operation) | none; `System.Diagnostics` only, no OpenTelemetry package, exporter or Serilog |
| Caching (I.3) | Cqrs | `Microsoft.Extensions.Caching.Hybrid` (MIT); never Redis or StackExchange: the application registers its own `IDistributedCache` |
| Localization (I.3) | Cqrs (`IModuleServices`, through which a Module's Feature assemblies are recorded) | ASP.NET Core framework reference only (its localization and request localization middleware); no NuGet package. Optional: the Proving Sample's Hosts reference none of it |
| Security.JwtBearer (I.3) | Security (2026-10-08: `PouryaKitClaimTypes`, which it writes, moved to Cqrs, which it reaches through Security, so Web reads the same claim by default) | `Microsoft.AspNetCore.Authentication.JwtBearer` (MIT; it brings the ASP.NET Core framework reference and `Microsoft.IdentityModel`); never Web |
| Web.OpenApi (I.3) | Web | ASP.NET Core framework reference; `Asp.Versioning.Http`, `Asp.Versioning.OpenApi`, `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` (all MIT); optional: Web references none of them, and no Swagger generator |
| Email (I.3) | Domain (the send returns a `Result`) | none: the contract only, no mail library |
| Email.MailKit (I.3) | Email; Cqrs (2026-10-17: the reference exists for its one-provider composition check, `ICompositionCheck`) | `MailKit` (MIT; it brings `MimeKit` and `BouncyCastle.Cryptography`, MIT); `Microsoft.Extensions.Options` (MIT, under the standing rule below); `System.Security.Cryptography.Pkcs` 10.0.13 (MIT), a security floor over the 10.0.0 MimeKit 4.18.1 asks for, which CVE-2026-84008 and CVE-2026-70162 affect: outside the standing rule, so an owner decision; the floor is gone, since `MailKit` 4.18.2 brings `MimeKit` 4.18.2, which asks for 10.0.13 itself |
| Storage.AzureBlob (I.3 File storage) | Storage | `Azure.Storage.Blobs` (MIT; it brings `Azure.Core`, `Azure.Storage.Common`, `System.ClientModel` and, through Azure.Core, `Microsoft.Identity.Client`, all MIT, and no `Azure.Identity`), `Microsoft.Extensions.Options` (MIT, under the standing rule below); 2026-10-18, a dated deviation (an owner decision): `Apache.Arrow` 23.0.0 (Apache-2.0) and `System.Security.Cryptography.ProtectedData` 10.0.13 (MIT; *10.0.12 until 2026-10-24*) as licence floors over the 22.1.0 and 4.5.0 Blobs brings, which declare no licence expression |
| Testing | Modularity, Persistence.Postgres, Messaging.Wolverine.EFCore | Testcontainers, Respawn, xUnit v3, `Microsoft.Extensions.TimeProvider.Testing`; `WolverineFx.RuntimeCompilation` (2026-10-07, as ADR 0004 allows: test Hosts run Development from any build, while the Hosts reference it in Debug builds only; the one Kit package with it) |
| Templates (I.3 "Project template") | — | none: a `dotnet new` template package with content only. The solutions it generates reference the Kit's packages at its own version, their third-party packages at the versions of the Kit's `Directory.Packages.props`, and in their smoke tests `Microsoft.AspNetCore.Mvc.Testing` (MIT) and `WolverineFx.RuntimeCompilation` (MIT: the generated Hosts reference it in Debug builds only). Amended 2026-10-10: the smoke tests reference `PouryaKit.Testing`, which brings `WolverineFx.RuntimeCompilation` and, in the test project only, `PouryaKit.Modularity`, and `Microsoft.AspNetCore.TestHost` (MIT) in place of `Microsoft.AspNetCore.Mvc.Testing`; the monolith Host's closure stays without Modularity |
| Storage (I.3 File storage) | Domain; Cqrs (2026-10-18: the one-provider composition check, `ICompositionCheck`, moved here from Storage.S3 so both providers register through `AddPouryaKitFileStorage`) | none of its own: the closure holds Cqrs's Microsoft.Extensions packages and no AWS or Azure package, so a provider's SDK stays in its provider package |
| Storage.S3 (I.3 File storage) | Storage; Cqrs (2026-10-17: the reference exists for its one-provider composition check, `ICompositionCheck`; removed 2026-10-18, when the check moved to Storage) | `AWSSDK.S3` (Apache-2.0; it brings `AWSSDK.Core`, Apache-2.0), `Microsoft.Extensions.Options` (MIT, under the standing rule below: the options the Host binds and their validation at start) |
| ReverseProxy (I.3 Reverse proxy) | none: the proxy runs no Operation, so no Kit package, the dispatcher's included, reaches its closure | ASP.NET Core framework reference; `Yarp.ReverseProxy` 2.3.0 (MIT; it brings `System.IO.Hashing`, MIT) and `Microsoft.Extensions.ServiceDiscovery.Yarp` 10.11.0 (MIT; *10.10.0 until 2026-10-24*; it brings `Microsoft.Extensions.ServiceDiscovery`), the two packages the I.3 row names |
| Grpc (I.3) | Web (its begin-the-Operation convention and header codec; Web opens its codec's header names, the deciding Error, its entry shape and its refusal type to the package with `InternalsVisibleTo`, as Cqrs opens its internals to Web); Cqrs through it for its startup checks | ASP.NET Core framework reference; `Grpc.AspNetCore.Server` and `Grpc.Net.ClientFactory` 2.84.0 (Apache-2.0; they bring `Grpc.Net.Client`, `Grpc.Net.Common` and `Grpc.Core.Api`, Apache-2.0). Not the `Grpc.AspNetCore` metapackage the I.3 row names (2026-10-18): its nuspec carries `Grpc.Tools` with `include="All"` into every consumer, so `Grpc.Tools` stays out of the closure, and a project that compiles a `.proto`, such as a Module's Contracts, references it with private assets beside `Google.Protobuf` 3.36.2 (BSD-3-Clause) and `Grpc.Core.Api` |
| Aspire (I.3 Aspire resources) | — | AppHost-only: `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Redis`, `Aspire.Hosting.RabbitMQ` 13.6.4 (MIT; *13.6.1 until 2026-10-24*; they bring `Aspire.Hosting` and its closure, every package with a permissive licence expression); `MessagePack` 2.5.307 (MIT), a security floor over the 2.5.302 `Aspire.Hosting` 13.6.1 asks for, which GHSA-qhrr-8q5h-9q3h affects (removed, since `Aspire.Hosting` 13.6.4 asks for 2.5.307 itself); `Fractions` 8.3.2 (MIT), a licence-expression floor over the 7.3.0 `KubernetesClient` 19.0.2 asks for, which ships its BSD-2-Clause licence as a file that `LicenceTests` cannot read: both outside the standing rule, so owner decisions; delete each once `Aspire.Hosting` asks for a version past it. `PackageGraph` keeps every Aspire package out of every other Kit package's closure |
| Realtime (I.3) | Web, Persistence.EFCore (which supersedes the I.3 row's `→ Messaging`: the SSE endpoint takes its caller's Actor and tenant through Web's Operation Context, and a push joins the EF Unit of Work's transaction) | ASP.NET Core framework reference (the server-sent events result); `Npgsql` (PostgreSQL License, the version Persistence.Dapper pins) for the listener's connection. Optional: no SignalR, message store, broker or Redis |
| Reporting (I.3 Reporting and export) | Domain (a refused layout is a `Result` with an Error) | `PDFsharp-MigraDoc` 6.2.4 (MIT; it brings `PDFsharp` 6.2.4, MIT, and Microsoft.Extensions logging and DI abstractions 8.0.x, MIT), `DocumentFormat.OpenXml` 3.5.1 (MIT; it brings `DocumentFormat.OpenXml.Framework` and `System.IO.Packaging`, MIT) and `CsvHelper` 33.1.0 (`MS-PL OR Apache-2.0`, taken under Apache-2.0), the renderer's libraries. PDFsharp 6.2.4 is its last MIT release: 6.5.0, the latest stable on 2026-10-18, and the 7.0 previews are `AGPL-3.0-only`, which `LicenceTests` refuses, so a bump past 6.2.4 is an owner decision. `System.Security.Cryptography.Pkcs` 10.0.13 (MIT), a security floor over the 8.0.1 PDFsharp 6.2.4 asks for, which CVE-2026-84008 and CVE-2026-70162 affect: outside the standing rule, so an owner decision. No QuestPDF, ClosedXML, second file library or storage package: the renderer returns the file's bytes and stores nothing |
| AI (I.3) | Cqrs, Modularity (the Gateway tool runs a call through a Module's `IGatewayProcessor<TModule>`; Cqrs gives the Operation Context and the one-client composition check) | `Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.OpenAI` 10.11.0 (MIT; the adapter brings `OpenAI` 2.14.0 and `System.ClientModel` 1.15.0, MIT), `Microsoft.Extensions.Http` and `Microsoft.Extensions.Options` (MIT, under the standing rule below); `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows` 1.26.0 (MIT; *1.25.0 until 2026-10-24*; the agent capabilities the owner's 2026-10-06 answer 3 gives the Microsoft Agent Framework, which bring `Microsoft.ML.Tokenizers` 2.0.0 and three `Microsoft.Extensions.*` abstractions, MIT, and `OpenTelemetry.Api` 1.18.0, Apache-2.0); `ModelContextProtocol.Core` 2.3.0 (Apache-2.0; *2.2.0 until 2026-10-24*; the official MCP C# SDK's client, which brings nothing the closure lacked); no `OllamaSharp` and no `Azure.AI.OpenAI`, and no resilience package: the provider's HTTP client keeps the Host's pipeline |
| AI.Postgres (I.3 AI) | AI (the Host's embedding generator), Persistence.Postgres (its EF Core migrations, for the collection helper, and Npgsql through its provider; the vector store writes on the EF Unit of Work's connection through Persistence's `IUnitOfWorkConnection`) | none: no `CommunityToolkit.VectorData.PgVector`, which I.4 named, since its collection writes on a connection of its own and so commits outside the Unit of Work (ADR 0003), and no `Pgvector` client, since an embedding goes as a `real[]` that the server's pgvector casts; `PackageGraph` keeps `Pgvector` and this package out of every other Kit package's closure, so a Host without it takes no pgvector |
| Persistence.Marten (I.3 Event-sourced write model) | Cqrs, Persistence (2026-10-18, ADR 0011's packages as built amendment: the Outbox step is a seam, `IStreamOutbox`, which Messaging.Wolverine.Marten fills, so the I.3 row's `→ Messaging` is not taken; `UnitOfWorkErrors` moved to Persistence for both Units of Work) | `Marten` 9.53.0 (MIT, an owner decision; *9.46.0 until 2026-10-24*; it brings `JasperFx`, `JasperFx.Events`, `Weasel.Core`, `Weasel.Postgresql` and `Weasel.Storage`, MIT, `NetTopologySuite` BSD-3-Clause, and the `JasperFx.Events.SourceGenerator` analyzer, which runs over every project that references the package); `Npgsql` 10.0.3 (PostgreSQL License), the Kit's line over the 9.0.4 Marten brings, since the Unit of Work runs the Kit's statements on Npgsql itself; never Wolverine or EF Core |
| Messaging.Wolverine.Marten (I.3 Event-sourced write model) | Messaging.Wolverine, Persistence.Marten | `WolverineFx.Marten` 6.52.0 (MIT; *6.48.3 until 2026-10-24*; it brings `WolverineFx.Postgresql`, the PostgreSQL message store Marten's integration builds, and `Microsoft.IO.RecyclableMemoryStream`, MIT); never EF Core |
| Reporting.Module (I.3 Reporting and export) | Modularity, Persistence.Postgres, Messaging.Wolverine.EFCore, Web: the stack a sample Module uses; Persistence.Dapper for the job read, as every sample query reads; Validation for its request validator; Security for `IActorPermissions`, through which the Host grants an Actor its tenant's jobs; Reporting and Storage, the renderer and the storage contract it generates through (2026-10-18: the first Module the Kit ships as a package, so the first row of a Module) | ASP.NET Core framework reference for its endpoints; no package of its own, and no storage provider or SDK: the Host composes its one provider |
| Messaging.Wolverine.Sagas (I.3 Saga) | Messaging.Wolverine (2026-10-18: it opens its internals to the package with `InternalsVisibleTo`, as Web opens its codec to Grpc, for its subscription registry, routing check, wire names and the Outbox's header write, which a saga's route, fan-out, checks and emissions use) | `WolverineFx.Postgresql` 6.52.0 (MIT, the Kit's line; *6.48.3 until 2026-10-24*; it brings `WolverineFx.RDBMS` and `Weasel.Postgresql`, MIT, `NetTopologySuite`, BSD-3-Clause, and `Npgsql` 9.0.4, PostgreSQL License, which a Host with Persistence.Postgres resolves at the Kit's 10.0.3); optional; never EF Core or Marten |

Amended on 2026-10-05, each addition MIT and already present in every Host:

- Modularity references `Microsoft.Extensions.DependencyInjection`: the options that validate the
  container at build live only there, and `AddPouryaKitModules` turns them on in every environment. *Moved to Cqrs with
  the validating factory (2026-10-07), as the table records; `AddPouryaKitModules` turns them on through
  Cqrs's `AddPouryaKitCompositionChecks`.*
- Persistence.Dapper references `Microsoft.Extensions.Configuration.Abstractions`: the read
  connection is registered by connection name, as `UsePouryaKitPostgres` is. It also references
  `Microsoft.Extensions.DependencyInjection.Abstractions` explicitly: its registration call
  on a Module's builder needs it, and the transitive copy Npgsql brings is not the pinned version. Its closure still
  holds no EF Core.
- Testing references `Microsoft.Extensions.TimeProvider.Testing`: the test clock of ADR 0005.
- Persistence.EFCore also references Persistence and `Ardalis.Specification.EntityFrameworkCore`, as G.4 decided.

Standing rule (owner, 2026-10-06), extending the amendments above: an MIT-licensed `Microsoft.Extensions.*`
package may be referenced where a package needs it, and is added to this table in the same change. Any other
reference outside the table stays an owner decision.

Checks against your rules: Dapper does not reference EF. Postgres does not reference Wolverine. Web
does not reference Modularity. Nothing chooses an identity provider. `Core` exists since Wave 2 and is capped at three public types by an architecture test; there is no `Utils` (corrected 2026-10-07).
Module `Contracts` projects reference nothing from the kit: public events are plain records.

Notes on the choices:

- **Wolverine is split in two.** `PouryaKit.Messaging.Wolverine` is transport- and database-neutral, so a
  broker-only service does not pull EF or a message store. `PouryaKit.Messaging.Wolverine.EFCore` adds the
  outbox commit strategy, receipts and the PostgreSQL store. With one container it needs only
  "which module is this scope" from `OperationContext`, so neither depends on Modularity and the
  proposal's long bridge name disappears. Known cost: `WolverineFx.EntityFrameworkCore` pulls
  `Microsoft.EntityFrameworkCore.Design` at runtime.
- **The commit sequence is fixed and owned by the unit of work:**
  1. handler returns; a failed `Result` or rollback-only flag ends here with rollback;
  2. drain domain events sequentially to a fixed point (depth guard); a failed handler `Result`
     stops the drain, and the rollback-only flag is checked again after the drain — a nested
     command that failed inside a reaction rolls everything back even if the reaction ignored it;
  3. prepare persistence: audit fields, soft-delete conversion, change detection, one `Version`
     bump per changed aggregate;
  4. build snapshots from that final state — a deleted aggregate yields a versioned tombstone;
  5. write the outbox (domain notifications and snapshots), save and commit once.
  Nothing mutates exported fields after step 3. The sequence as built, with its rollback-only checks and where the
  Snapshots are built, is in ADR 0003's amendment of 2026-10-06.
- **`IUnitOfWork` lives in `PouryaKit.Cqrs`**, because the pipeline that calls it lives there. That removes
  `Persistence.Abstractions`, and lets a single-module vertical-slice API use Cqrs + EFCore without
  Modularity.
- **`PouryaKit.Persistence.Dapper` is small on purpose.** It exists so Dapper never arrives through EF.
- **Security is not a package in v1.** The actor is part of `OperationContext`; `PouryaKit.Web` fills it
  from `ClaimsPrincipal`; the application configures JwtBearer itself. Cybernod's Security package
  shows the risk of doing more: subscription claims and an admin bypass are baked into shared code
  (`CustomClaims.cs:26-66`, `FeatureAuthorizationHandler.cs:11-18`). *Corrected 2026-10-07: Wave 2 adds
  `PouryaKit.Security` → `Cqrs` with permission contracts and an optional `IAuthorizer` implementation only; the
  authenticated `FallbackPolicy` stays in `PouryaKit.Web`, which owns the HTTP boundary, and
  `PouryaKit.Security.JwtBearer` configures JwtBearer since 2026-10-07.*

### Full target catalogue

The goal is a complete kit, not a minimal one. The 13 projects above (12 until G.4; corrected 2026-10-06) are **wave 1** because every
other capability sits on top of the execution core, and that core is the part no source gets right.
Waves are a build order, not a cut list. A capability becomes a package only where the kit adds
behaviour or a convention of its own; where .NET already ships the capability, the kit ships a
registration recipe inside the template instead of a wrapper.

| Capability | Form | Wave | Best source | Kit's own contribution |
|---|---|---|---|---|
| Domain, CQRS, validation, modularity, persistence, messaging, web, testing | 13 packages above | 1 | section B | the execution core |
| API versioning, OpenAPI, Scalar | `PouryaKit.Web.OpenApi` | 2 | Woo `ApiVersioningExtensions.cs`, Cybernod `OpenApi/*` transformers | per-module route groups and version sets; security transformer that adds requirements |
| Authorization | `PouryaKit.Security` | 2 | Cybernod `AuthExtensions.cs:47-59`, `UserAccessor` | actor/permission contracts, pipeline authorization step (also covers background commands, which Cybernod never authorizes), fallback policy on by default |
| JWT bearer adapter | `PouryaKit.Security.JwtBearer` | 2 | Cybernod `ClerkExtensions.cs:95-149` minus Clerk | claim normalisation into `OperationContext`; no provider SDK |
| Request idempotency | in `PouryaKit.Cqrs` + `PouryaKit.Persistence.EFCore` | 2 | Ultrabusiness `IdempotencyBehavior` (design only) | key reserved inside the command transaction; a failed command is not replayed as success |
| Caching | `PouryaKit.Caching` (+ Redis via HybridCache's `IDistributedCache`) | 2 | Cybernod `CachingBehavior`, `ICacheableQuery` | query caching + invalidation steps over HybridCache; never cache failed Results; keys scoped by actor explicitly |
| Scheduled / delayed / recurring work | `PouryaKit.Messaging.Wolverine` feature | 2 | Cybernod internal commands; Wolverine scheduled messages | durable replacement for in-memory Quartz stores (Cybernod loses jobs on restart) |
| Multi-tenancy | `PouryaKit.MultiTenancy` | 3 | Ultrabusiness Owner/CompanyBranch (idea only) | tenant in `OperationContext`, EF query filter, receipt key, message header (*2026-10-16: built as a registration across `Domain`, `Cqrs` and `Persistence.EFCore`, no package, as I.3 makes it; the receipt key stays as built. 2026-10-17: the save's ownership check, the tenantless-delivery refusal and the composition refusals added to `Persistence.EFCore`, `Messaging.Wolverine` and `Messaging.Wolverine.EFCore`*) |
| Observability | template content + small `PouryaKit.Observability` | 2 | Aspire ServiceDefaults; Cybernod `ObservabilityPipelineBehavior` | command/query activity + metrics step, trace context through outbox headers |
| Health, resilience, service discovery | template content (Aspire ServiceDefaults) | 2 | Aspire 13.6 template | none — native; liveness must not include external dependencies |
| Logging provider (Serilog) | template option | 2 | Cybernod bootstrap | none |
| Aspire AppHost resources | `PouryaKit.Aspire` (AppHost-only) | 3 | CNK `AspireIntegrations` | only resources actually needed |
| RabbitMQ transport, microservice profile | `PouryaKit.Messaging.Wolverine.RabbitMq` | 2 (contracts are transport-neutral from wave 1) | CNK/Woo topology helpers | per-service queue naming, durable outbox on routes |
| Realtime (SSE) | one optional `PouryaKit.Realtime` package | 3 | `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.SSE/Extensions/SseEndpointExtensions.cs:58` | SSE from a Notification Handler after commit; a client connected to any instance receives the event; no process-local connection list, SignalR package or second push channel |
| File storage, email | optional packages | 3 | Ultrabusiness S3, CNK Email | thin, provider-per-package |
| Reporting and export | one optional renderer package and one opt-in report Module | 3 | `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.Reporting/Extensions/ReportingExtensions.cs:88-122`; `src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Infrastructure/Reporting/Extensions/ReportingInfrastructureExtensions.cs:38-41` | layout primitives, fonts, branding, PDF, CSV and spreadsheet rendering; Host registers the report Module once; stored report jobs use the Kit's scheduler and file storage; `POST /reports` returns 202 and a job id; layouts stay in the adopting Module; PDF licence check remains open under ADR 0002 (*closed 2026-10-18: PDFsharp with MigraDoc 6.2.4, the last MIT release*) |
| Number sequences, AI | optional packages, only if reused across projects | 4 | Ultrabusiness, Cybernod | decide per capability; AI stays app-level by default |
| Project template | `dotnet new` template | 2 | the proven sample | generated from the sample, never maintained separately |

---

## D. Consuming application

This section is the plan's sketch; the API as built is in the Kit's skill, `.agents/skills/pourya-kit/`.

Layout follows Cybernod's real structure (documented from `Cybernod.Modules.Assets/AssetDiscovery`
and `Cybernod.Modules.Identity/User`), with its naming drift normalised to one spelling:
`Data/Configurations`, `Data/Repository`, `Events/DomainEvents`.

```text
src/
├── Api/App.Api/Program.cs
└── Modules/Catalog/
    ├── Catalog.Contracts/Products/Events/V1/{ProductRenamedV1,ProductSnapshotV1}.cs
    └── Catalog/
        ├── CatalogModule.cs
        ├── Products/
        │   ├── Entities/Product.cs
        │   ├── ValueObjects/ProductId.cs
        │   ├── Data/{Configurations/ProductConfiguration.cs, Repository/{IProductRepository,ProductRepository}.cs}
        │   ├── Events/{DomainEvents/ProductRenamedDomainEvent.cs, Notifications/ProductRenamedNotificationHandler.cs, ProductSnapshotMapper.cs}
        │   └── Features/
        │       ├── CreateProduct/{CreateProductCommand,…Validator,…Handler,…Endpoint}.cs
        │       └── GetProduct/{GetProductQuery,…Handler,…Endpoint}.cs
        └── Infrastructure/Data/{Context/CatalogDbContext.cs, Migrations/}
```

Two differences from Cybernod are deliberate: a `Contracts` project per module replaces
`Modules.Common` as the place for public events, and modules never call each other over HTTP
(Cybernod does: `Common/HttpClients/Identity/IdentityApiClient.cs:42-45`).

### Feature path (all `PouryaKit.*` APIs proposed)

```csharp
// Products/ValueObjects/ProductId.cs
internal sealed record ProductId(Guid Value) : TypedId<Guid>(Value);

// Products/Entities/Product.cs
internal sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; } = "";

    public static Result<Product> Create(ProductId id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ProductErrors.NameRequired;
        var product = new Product { Id = id, Name = name.Trim() };
        product.Raise(new ProductCreatedDomainEvent(id, product.Name));
        return product;
    }
}

// Products/Features/CreateProduct/
internal sealed record CreateProductCommand(Guid ProductId, string Name) : Command<Guid>;

internal sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();          // typed-ID constructor rejects Guid.Empty by throwing
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

internal sealed class CreateProductCommandHandler(IProductRepository products)
    : CommandHandler<CreateProductCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(CreateProductCommand command, CancellationToken ct)
    {
        var product = Product.Create(new ProductId(command.ProductId), command.Name);
        if (product.IsFailure) return product.Errors;

        await products.AddAsync(product.Value, ct);
        return product.Value.Id.Value;             // no SaveChanges, no transaction, no outbox
    }
}

internal sealed class CreateProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) => routes.MapPost("/products",
        async (CreateProductBody body, IGatewayProcessor<CatalogModule> gateway, CancellationToken ct) =>
            (await gateway.SendAsync(new CreateProductCommand(body.ProductId, body.Name), ct))
                .ToHttpResult(id => Results.Created($"/products/{id}", id)));
}

// Products/Events/Notifications/ProductRenamedNotificationHandler.cs — runs after commit, from the
// outbox, in its own scope; this is where the module talks to the outside world
internal sealed class ProductRenamedNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<ProductRenamedDomainEvent>
{
    public Task<Result> Handle(ProductRenamedDomainEvent e, CancellationToken ct)
    {
        events.Publish(e.Id, new ProductRenamedV1(e.Id, e.OccurredOn, e.ProductId.Value, e.NewName));
        return Result.SuccessTask;
    }
}
// A same-module reaction that must share the transaction is an IDomainEventHandler<T> instead.

// Products/Events/ProductSnapshotMapper.cs — state transfer; the kit calls this once per changed
// Product after all domain reactions finish and the version is final
internal sealed class ProductSnapshotMapper : ISnapshotMapper<Product, ProductSnapshotV1>
{
    public ProductSnapshotV1 Map(Product p) => new(p.Id.Value, p.Name, p.IsActive, p.IsDeleted, p.Version);
}

// Products/Features/GetProduct/GetProductQueryHandler.cs — Dapper read
internal sealed class GetProductQueryHandler(IReadConnectionFactory db)
    : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ProductDto>(new CommandDefinition(
            "select id, name from catalog.products where id = @Id and is_deleted = false",   // Dapper bypasses EF query filters
            new { Id = query.ProductId },
            cancellationToken: ct));
        return row is null ? ProductErrors.NotFound : row;
    }
}

// Inventory/CatalogProducts/Features/ApplyProductChange/ProductChangedHandler.cs — receiving module
internal sealed class ProductChangedHandler(IInventoryCatalogStore store)
    : IIntegrationEventHandler<ProductSnapshotV1>
{
    public async Task<Result> Handle(ProductSnapshotV1 message, CancellationToken ct)
    {
        // store applies the snapshot only if message.Version is newer: receipts stop duplicates,
        // the aggregate version stops an older event overwriting a newer one
        await store.ApplySnapshotAsync(message, ct);   // no receipt, no ack, no transaction
        return Result.Success();
    }
}

// CatalogModule.cs
public sealed class CatalogModule : IModuleDefinition
{
    public string Name => "catalog";
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.AddFeaturesFrom(typeof(CatalogModule).Assembly);   // handlers, validators, endpoints, repositories
        module.Publish<ProductRenamedV1>("catalog.product-renamed.v1");
        module.PublishSnapshot<Product, ProductSnapshotV1>("catalog.product-snapshot.v1");
    }
}
// InventoryModule: module.Subscribe<ProductSnapshotV1, ProductChangedHandler>("inventory.catalog-products");

// Program.cs
builder.AddServiceDefaults();                       // Aspire template project, not a kit package
builder.Services.AddPouryaKitWeb();
builder.Services.AddPouryaKitModules(m => { m.Add<CatalogModule>(); m.Add<InventoryModule>(); });
builder.Host.UsePouryaKitWolverine(connectionName: "app");
app.MapPouryaKitModuleEndpoints();
```

### Who does what

| Feature author writes | Kit does once |
|---|---|
| Aggregate, typed ID, errors, domain events | Result/ID/event primitives, stable event id and time |
| Command, validator, handler | Dispatch, logging, authorization hook, validation, module scope |
| Repository + EF mapping | Transaction, single save/commit, rollback on failed Result |
| Domain-event handlers (same transaction), notification handlers (after commit), snapshot mappers | Sequential drain to fixed point, single version bump, snapshot finalization, outgoing buffer, outbox write in the same commit |
| Dapper SQL + DTO, including the soft-delete (and later tenant) predicate | Connection lease, typed-ID handlers |
| Receiving handler | Delivery into a fresh module scope, receipt in the same transaction, retry / dead-letter, context from headers |
| Endpoint, module declaration | Result → HTTP, endpoint discovery, startup validation |

---

## E. Implementation plan

Each phase ends with tests passing against PostgreSQL in Testcontainers. A phase is not done on
compilation.

**Phase 0 — spikes (throwaway code, decide the open design points)**
- S1: one container, two DbContexts/schemas in one database. Owned unit of work commits through
  `IDbContextOutbox<T>`. Kill the process after commit and before flush; confirm recovery.
  Confirm behaviour with Wolverine's Lightweight vs Eager EF modes and with no EF retrying strategy.
- S2: fan-out. `MultipleHandlerBehavior.Separated`/sticky handlers per subscriber vs the proposal's
  per-consumer delivery envelope. Pick the one with independent retry and the least owned code.
- S3: dispatcher. Owned dispatcher vs `Mediator` 3.x with `internal` handlers in module assemblies.
- Done when: each choice is recorded in a short ADR with the test that decided it.

**Phase 1 — `PouryaKit.Domain`, `PouryaKit.Cqrs`, `PouryaKit.Validation`**
- Adopt: Cybernod primitives, Result, command shapes. New: dispatcher, pipeline.
- Done when: unit tests cover Result invariants, typed-ID equality by type, event id/time stability
  through JSON round-trip, validation returning all errors, pipeline order.

**Phase 2 — `PouryaKit.Persistence.*`, `PouryaKit.Testing`** (needs 1)
- New: unit of work. Adapt: converters, interceptors, Postgres registration.
- Done when, with two aggregates and no per-aggregate helper: failed Result rolls back (rows 2);
  no-event command saves (3); chained events drain once, a cycle fails visibly (5); handlers are
  sequential (6); nested command joins, and a nested failure of any kind (handler, validation,
  authorization) forces rollback even when the outer handler — or a domain event handler during
  the drain — ignores it, with business changes and outbox rows both gone (12); a transient
  failure never replays on a used DbContext (13); Dapper read lease is explicit and a Dapper write
  can join the module transaction (15); reading a soft-deleted row through Dapper returns `NotFound`
  and list queries omit it; two scopes updating the same aggregate — once with events,
  once without — produce exactly one commit and one `Conflict`.

**Phase 3 — `PouryaKit.Modularity`, `PouryaKit.Web`, sample Catalog** (needs 2)
- Adapt: Cybernod gateway API, endpoint convention, ProblemDetails mapping.
- Done when: module scopes are created and disposed per operation; a missing module fails startup;
  `ValidateOnBuild` passes; architecture tests (with negative cases) reject cross-module references,
  handler-level `SaveChanges`, and transport types in features; an architecture test rejects Dapper writes to aggregate tables; HTTP tests cover 400/404/409 mapping, including an
  empty id returning a validation response with nothing persisted.

**Phase 4 — `PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, sample Inventory + Billing** (needs 3, S1, S2)
- Packages: `PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`. Use: WolverineFx. Extract: Woo policy block. New: commit strategy, delivery handler, receipts, headers.
- Done when: rollback leaves no outgoing message (7); receipt and effect commit together (8);
  duplicate and concurrent duplicate deliveries produce one effect (9); restart after commit delivers,
  crash after receiver commit does not repeat the effect (10); Inventory failing does not re-run
  Billing (11); event id and timestamp are identical at sender and receiver (14); reverse-order and concurrent deliveries of two versions
  leave the newer snapshot; several changes to one aggregate in one transaction (nested command
  plus domain reaction) emit exactly one snapshot whose version equals the persisted version; a soft delete emits a
  tombstone, and an older live snapshot arriving after the tombstone does not resurrect the row; actor and
  correlation arrive in the receiver without an HTTP request (16).

### Phase 5 packaging and releases

Needs Phase 4. Wave 1 is merged. The scope is packaging, versioning and NuGet packages with standard tooling: the existing shared package validation and the standard private GitHub Packages publisher are delivered, and they replace the custom publication protocol and preflight machinery. Compatibility automation, named cuts and content-verified recovery are out of scope and need a new scoped brief. Section G and the ADRs remain unchanged.

Keep central package management, MinVer as the only version engine, SourceLink, `EnablePackageValidation`, MIT and the third-party notices file in each nupkg. *Superseded in part 2026-10-26: that file was removed, so each nupkg carries the MIT `LICENSE` alone (E, [Provenance and licensing](#provenance-and-licensing)).* All thirteen shipped Kit packages share one version. Private GitHub Packages comes first. Nuget.org activation and stable `1.0` each require a later owner decision.

MinVer uses minimum major/minor `0.1`, tag prefix `v` and default prerelease identifiers `preview.0`. Fetch full history and tags. Untagged main commits use Git height, such as `0.1.0-preview.0.H`. Owner-created `v0.x` preview/rc tags on current main supply exact named versions when the owner then runs CI manually on main. No workflow creates a tag or GitHub Release, and no version override or second calculation is added. See [MinVer's pinned README](https://github.com/adamralph/minver/blob/9f485aabb9936efd8fb8b633ae09ae4d0659c94f/README.md#L179) and [private package guidance](private-packages.md#versions-and-triggers).

| Trigger | Active behaviour |
|---|---|
| Pull request, tag push or manual CI on a non-main branch | Read-only build, tests, pack, dependency checks and package-only consumers. No publishing, tag/release writes or release credentials. |
| Successful main push CI | Publish original validated MinVer `0.x` preview/rc packages to the fixed private feed. |
| Manual CI on main after an owner-created prerelease tag | Validate that main SHA with full tags, then publish its original packages at MinVer's tag version. |
| Manual private publication on main | Select the original successful main CI run, attempt and SHA. Download the same retained packages and manifest. |

The read-only authorization job requires successful CI and AI/package-validation jobs, the fixed private repository, exact main source SHA and attempt, and a producer event of `push` or `workflow_dispatch`. PR/tag-head artifacts cannot authorize publication. The publisher uses trusted main code, official artifact download, the original receipt/inventory/hash checks, standard `dotnet nuget push` and a clean all-thirteen private-feed restore. It never builds replacement packages. The existing literal concurrency group disables cancellation of an active publisher; GitHub's one pending slot is not a durable queue.

`--skip-duplicate` skips HTTP 409. It does not verify matching remote contents or fulfill the original identical-content recovery guarantee.

**Active acceptance:**

1. Package metadata and consumer proof remain the existing path. Evaluate the nonempty thirteen-package inventory, build Release once, test with `--no-build`, pack with `--no-build`, check metadata/SourceLink and package graph rules, and restore/build every package and supported profile in clean package-only consumers.
2. CI remains read-only and preserves the tested SHA, MinVer version, package inventory, hashes and original artifacts. Successful main producer metadata authorizes a separate package-write job; failure or PR provenance cannot authorize it.
3. Publish originals to the fixed private feed with the ephemeral job token, preserve the manifest, and restore all thirteen exact versions with fresh caches. Keep public publication and tag/Release writes disabled.

**Out of scope:** packaged public API comparison, reviewed compatibility/progression and migration notes, approved immutable named cuts, tagged validation, tag/package/manifest version agreement, GitHub Release announcement only after complete publication, remote raw-byte/SHA conflict checks and partial-publish identical-content recovery from original artifacts. These are not guarantees of standard duplicate skipping. Resuming that work needs a new scoped brief; MinVer remains the sole version engine.

**Phase 6 — template and second profile** (optional, after 5)
- `dotnet new` template extracted from the proven sample; single-service profile with RabbitMQ.

**Out of scope: existing projects.** The kit is for new projects. Cybernod, Ultrabusiness and the
other inspected codebases are sources of ideas only; nothing in this plan changes, migrates or
refactors them.

### Provenance and licensing

- CNK, Woo, food-delivery, vertical-slice (Hadeli) and mm-ddd (Grzybek) are MIT with no NOTICE files.
  Keep each copyright line and the MIT text in the third-party notices file; record source commit and
  path per extracted file. *Superseded 2026-10-26: the third-party notices file and the
  provenance records are removed, so the repository keeps only material about the Kit itself. The one third-party
  file adapted into the Kit, `samples/ModularMonolith/src/ModularMonolith.ServiceDefaults/Extensions.cs` from .NET
  Aspire, carries Aspire's MIT notice in its header; Cybernod, the source of the other adapted code, is the owner's
  own; Roboto's Apache licence ships inside `PouryaKit.Reporting` (`src/PouryaKit.Reporting/Fonts/LICENSE.txt`).*
- **Rewrite, do not copy:** files derived from `oskardudycz/EventSourcing.NetCore` (CC BY-SA 4.0 —
  `TelemetryPropagator.cs` in CNK and vertical-slice, CNK `SubscribeToAllBackgroundWorker.cs`, Woo
  `EventStoreDB/Extensions.cs`); Stack Overflow-derived helpers in CNK (`ObjectExtensions`,
  `TypeExtensions`, `AssemblyExtensions`, `ReflectionUtilities`); code from unlicensed repos (Woo
  `EFCore/Extensions.cs`, CNK `SqlKataExtensions.cs`). None of these is on the adoption list above.
- **Ultrabusiness:** treat as reference only until ownership is confirmed. The plan ports its test
  *scenarios* and reimplements its patterns; it copies no file.

---

## F. Evidence limits and open decisions

**Demonstrated by source:** every file:line claim above, read at the listed commits.

**Covered by existing tests:** only Ultrabusiness Treasury (outbox atomicity, rollback, redelivery
suppression, replay after commit, dead-letter — on SQL Server with its own outbox) and mm-ddd /
food-delivery happy paths. Nothing tests failed-Result rollback, concurrent duplicates, or
multi-subscriber failure anywhere.

**Inferred, not verified:**
- Wolverine internals: per-retry scope (S2). *Resolved, ADR 0006: each attempt runs in a new scope
  that the Kit opens.* Send timing without an enrolled transaction and queue naming defaults were
  observed in Spike S1 against WolverineFx 6.46.0; see ADR 0004.
- Whether Wolverine can route one message to several kit-registered consumers with independent ack
  without app-specific handler classes (S2). *Resolved, ADR 0006: yes, with one route and durable
  local queue per subscription and one pre-built Kit handler per message type.*
- Whether `Mediator` 3.x handles `internal` handlers across assemblies (S3). *Resolved, ADR 0002: only
  with `InternalsVisibleTo` to the host or a generator inside every module.*
- How Lucky Penny §4.2.e.3 would be applied to your case is a legal question; the text is clear enough
  that I would not build on it.

**Verification boundary:** the original reference-project comparison in this section is historical
static evidence. Current Kit build, database and runtime proof is recorded in the
[acceptance map](acceptance.md) and the [completed active Phase 5 scope](#phase-5-packaging-and-releases).
Remaining unverified work requires its own proof. The private-fork verification exception remains.

**Decided by the owner (2026-10-04):**
1. **One host container.** Modules keep their own composition root, gateway, operation scope,
   DbContext and schema; no private `ServiceProvider` per module.
2. **Adoption rule.** Take the best implementation from any shared source and customise it, but the
   result must be one consistent design — one implementation per concern, one naming scheme. The
   kit's own docs and provenance records cite upstream repository, commit and repo-relative path,
   never a local machine path. *Superseded in part 2026-10-26: the provenance records are removed (E,
   "Provenance and licensing"); the docs still name no local machine path.* Ultrabusiness ownership is unconfirmed, so it is adopted at pattern
   level (reimplemented, with its test scenarios ported); the public MIT samples may be copied with
   attribution. For every concern, compare verified CNK/Woo behaviour and the Kit's requirements
   across API/design, correctness and runtime failure behaviour, performance where relevant,
   tests, developer experience/docs, security/operations and CI/packaging/releases. Record an
   adopt/adapt/reject rationale with repo/commit/path, proof boundary and implementation/maintenance
   cost. Choose the strongest requirement-aligned behaviour per concern; no whole-repository winner
   and no extra abstraction merely to match a reference. Require evidence for correctness and keep
   implementation and maintenance cost proportionate. Phase 5 applies this rule to release automation.
3. **Private first, public later.** `0.x` on GitHub Packages, then nuget.org under MIT.
4. **Monolith and modular monolith first, broker-ready from day one.** Messaging contracts, wire
   names, headers, routing declarations and receipts are transport-neutral in wave 1; the RabbitMQ
   transport package moves up to wave 2.

5. **Name and prefix: `PouryaKit`.**
6. **.NET 10 only**, latest C# language version, current .NET idioms throughout.
7. The "no local files" rule is confirmed as a docs and provenance rule. *Superseded in part 2026-10-26: a docs
   rule, since the provenance records are removed.*

---

## G. Design decisions from the grilling session (2026-10-04)

These are final unless implementation produces concrete evidence against one, in which case the
evidence goes back to the owner. Where earlier sections differ, this section wins.

1. **Failures.** Expected business failures are `Result`; exceptions are for bugs and infrastructure.
   `CheckRule` returns a `Result`.
2. **Three kinds of reaction, kept distinct** (Cybernod's model, made durable):
   - *Domain Event Handler* — inside the module, same transaction, coordinates aggregates.
   - *Notification Handler* — after commit, driven from the module's outbox, each handler retried
     and recorded independently. This is where email, SSE and **publishing Integration Events** happen.
   - *Integration Event Handler* — in the receiving module, driven from its inbox, effect and receipt
     committed together.
   The outbox therefore stores the domain event (as a notification) in the same commit as the
   business change; integration events are published by notification handlers with the domain
   event's stable id. Snapshots are still produced by the kit at commit (section C).
3. **Typed IDs.** `TypedId<TValue>` for Guid, long, int and string; Guid v7 by default; EF Core
   conventions, Dapper type handlers and JSON converters registered by the kit. No ID framework
   beyond that.
4. **Repositories.** Generic `IRepository<TAggregate, TId>` plus Ardalis.Specification for the write
   side; an aggregate-specific repository may extend it when genuinely needed; reads normally go
   through Dapper. The generic repository stays small (add, remove, find by id, find/list/any/count
   by specification). To keep `PouryaKit.Domain` dependency-free, the repository and specification
   abstractions live in a new **`PouryaKit.Persistence`** package (references Ardalis.Specification);
   `PouryaKit.Persistence.EFCore` implements it. Wave 1 is now 13 packages. Verified 2026-10-04 from
   nuget.org: Ardalis.Specification 9.3.1 and Ardalis.Specification.EntityFrameworkCore 9.3.1 are MIT;
   the base package has no dependencies on modern .NET; the EF package requires EF Core ≥ 9.0.8 on
   its `net9.0` target (there is no `net10.0` target, so EF Core 10 compatibility is confirmed at
   first build). The repository is not archived; its last push was 2025-11-25.
5. **Module communication.** Same module: Domain Events. Across modules, asynchronous: Integration
   Events through outbox and inbox. Across modules, synchronous, only when genuinely required
   (permission, membership, availability checks): an interface published in the owning module's
   Contracts, served through its Gateway. HTTP/gRPC only across real process boundaries, never as a
   loopback inside one host.
6. **Authorization at trust boundaries.** Authenticate and authorize at the external entry point
   before the Operation runs. Work created by an authorized Operation carries Actor, tenant/company,
   correlation and causation in the Operation Context and is a trusted continuation; it is not
   re-authorized as the original user. Independent system work runs under an explicit system
   identity. The pipeline has an optional command-level authorization hook; nothing forces it.
7. **No mapper** in the kit; explicit mapping.
8. **Validation** is one pipeline step with one failure shape. Transport-only checks may stay at the
   transport boundary.
9. **Repository and tracker.** Private GitHub repository; issues as the tracker.
10. **Spikes** are deleted once the decision is recorded; a spike that proves an invariant worth
    protecting becomes a permanent integration or architecture test.
11. **The kit ships an AI skill/plugin** in the repository so developers and agents can use every
    capability; it is updated with each capability.
12. **New projects only.** The kit is not used to refactor or migrate any existing project.

## H. Workflow for every phase

The current process and completion gates are in `AGENTS.md`, "Operating model" and "Workflow for every ticket". Host routing and command examples are in `docs/agents/ai-setup.md`; multi-ticket execution is in `docs/agents/running-a-spec.md`.

---

## Recommended plan in one paragraph

Build an owned kit on .NET 10 with no mediator dependency, 13 packages in the first wave and the
full catalogue after it. Conventions come from Cybernod: typed IDs, aggregates, `Result`, command
shapes, business-area folders. One host container holds module-tagged scopes behind
`IGatewayProcessor<TModule>`; a small owned dispatcher runs a fixed pipeline. One Result-aware unit
of work is the only commit owner, with a fixed sequence: roll back on any failure (including nested
ones), drain domain events to a fixed point, prepare persistence (audit, soft delete, one version
bump), build one snapshot or tombstone per changed aggregate, then save and commit once through
Wolverine's outbox. EF Core/Npgsql for writes, Dapper for reads, native Wolverine with PostgreSQL
durable local queues, a business receipt per consumer committed with its effect, and
transport-neutral messaging contracts so RabbitMQ slots in at wave 2. Run the three spikes first,
prove everything on a Catalog → Inventory + Billing sample with the acceptance tests in Phases 2–4,
then publish `0.x` privately for use in new projects. Existing projects are not migrated.

---

## I. Catalogue amendment (2026-10-06)

Written after Wave 1 closed. It is the catalogue of
everything a Host may register after Wave 1. It adds to sections C and G and does not change them;
where an older row of the "Full target catalogue" differs, this section wins. Section G and the
decision records in `docs/adr/` are untouched; what this section contradicts in them is listed in I.6
as follow-up decisions and ADR amendments to record when the capability is built. Sources are cited
as `repository@commit path:line`; earlier Ultrabusiness evidence is cited as `Ultrabusiness@a85eacd`;
the SSE, reporting and localization additions use `Ultrabusiness@d685de6`
(private, pattern only, no file copied). Package facts were read from nuget.org and the libraries' own
repositories on 2026-10-06. *Waves 1 to 4 are built; the dated notes in I.3 record where the build departed from the rows.*

### I.1 The three compositions

| Composition | What it references | How Modules talk |
|---|---|---|
| **Monolith** | `PouryaKit.Cqrs`, `Validation`, `Persistence*`, `Web`; no `Modularity` | one Module in all but name |
| **Modular monolith** | the above plus `Modularity`, `Messaging*` | the Module Gateway in-process; Integration Events and Snapshots through the Outbox |
| **Service** | one Module in its own process, its own database, `Messaging.Wolverine.RabbitMq` | Integration Events over RabbitMQ; YARP or gRPC only for a call that crosses a process |

A Module's **business code** — aggregates, commands, queries, handlers, validators, repositories,
reads, Domain Event, Notification and Integration Event Handlers — is the same in all three. The
composition-specific parts are generated by the template (I.5): the Composition Root (`IModuleDefinition`
with `ModuleBuilder` in the modular monolith and the service; a plain registration in the monolith)
and the endpoint adapter (endpoints take `IGatewayProcessor<TModule>` in the modular monolith and the
service, `IDispatcher` in the monolith — today's sample endpoints are the former, `samples/ModularMonolith/src/Modules/Catalog/Catalog/Products/Features/CreateProduct/CreateProductEndpoint.cs:23`).
The monolith composition gets its own HTTP smoke test without `Modularity` registered. Inside one Host,
a call between Modules goes through the Gateway, never through YARP or gRPC loopback.

### I.2 Decisions applied

1. **Paging** (first follow-on after Wave 1; Wave 1 shipped an unpaged list).
   `PageRequest` (number, size, sort key, direction) and `Page<T>` (items, number, size, total,
   derived `TotalPages`, `HasNext`, `HasPrevious`) live in `PouryaKit.Cqrs` with no persistence
   dependency. `PouryaKit.Persistence.Dapper` runs the caller's `SELECT` with `OFFSET`/`LIMIT` and
   the caller's `COUNT`; the handler writes `WHERE`, its parameters and an **allow-listed** sort — a
   sort key that is not in the list is a validation `Error`, never SQL. `PouryaKit.Persistence.EFCore`
   applies the same request with `Skip`/`Take` after filtering and ordering. `PouryaKit.Web` returns
   one page shape. No Sieve and no filter string that becomes SQL. Shape adopted from
   `PouryaNoufallah96/cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Core/Messaging/Queries/Paging/PageList.cs:14-32`
   and the allow-listed sort in
   `src/BuildingBlocks/BuildingBlocks.Persistence.EfCore.Postgres/Extensions/DapperExtensions.cs:279-341`;
   rejected from the same sources: the Sieve helpers page the unfiltered, unsorted query with a
   synchronous count (`QueryableExtensions.cs:158-163`; `meysamhadeli/woo@149a3cf src/Core/Pagination/Extensions.cs:25-28`;
   `mehdihadeli/CloudNativeKit@e81d65e src/CloudNativeKit.Core/Extensions/QueryableExtensions.cs:44-47`),
   and the Dapper filter builder interpolates an unchecked field name into SQL
   (`DapperExtensions.cs:219-222,605-620`).

2. **Event sourcing** is in the catalogue as `PouryaKit.Persistence.Marten` (Marten 9.53.0 on
   PostgreSQL, MIT; 9.46.0 until 2026-10-24). An aggregate that needs it is a **Stream**: load by folding its events, append
   when new, append when the expected version matches. Every other aggregate stays an EF Core row.
   The Stream does not share the EF Unit of Work, the generic repository or the EF outbox.
   Evidence changes one detail of the wording "the stream's owner is Wolverine's Marten
   integration": Wolverine's aggregate-handler workflow and its Marten transactional middleware run
   only inside Wolverine-generated handlers
   (`JasperFx/wolverine@1f22835 docs/guide/durability/marten/event-sourcing.md:207-232`,
   `transactional-middleware.md:39-41`), which the Kit does not use (ADR 0002, 0006). The one path
   the Kit's own pipeline can use is `IMartenOutbox` plus `IDocumentSession.SaveChangesAsync()`
   (`docs/guide/durability/marten/outbox.md:79-117`). So the Stream's commit owner is a **Marten Unit
   of Work inside the Kit, committing through Wolverine's Marten outbox** — the same shape as the EF
   Unit of Work committing through the EF outbox (ADR 0004). The rest of the design, the spike and
   the contradictions with ADR 0003 and 0004 are in I.4 and I.6. EventStoreDB is not added;
   CloudNativeKit's subscribe-to-all worker and its ten-member `IEventStore` are not adopted
   (`mehdihadeli/CloudNativeKit@e81d65e src/CloudNativeKit.Persistence.EventStoreDB/Subscriptions/SubscribeToAllBackgroundWorker.cs:105-127,166`
   checkpoints after publishing and resubscribes with a blocking wait; `src/CloudNativeKit.Persistence.Marten/MartenEventStore.cs:74-78`
   passes sentinel versions into an overload that fails on Marten 9's default append mode).

3. **AI** is a real optional package, `PouryaKit.AI`, superseding the catalogue row that left AI in
   the application. Pattern source: `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.AI`.
   Design in I.4. The Kit's own contribution is that **a tool call is an Operation through the Module
   Gateway**; no reference has that — Ultrabusiness tools call repositories directly and its agents
   are built without a service provider, so a tool call there carries no Actor, no Operation Context
   and no authorization (`Services/ToolsFactoryBase.cs:22-39`, `Services/DefaultAgentService.cs:79`).

4. **Documentation UI is Scalar only.** No Swagger, no Swashbuckle, no AsyncAPI.

### I.3 Capability table

Form: **P** = its own package · **R** = a registration method on an existing package · **T** =
template recipe (native .NET; the Kit adds nothing) · **A** = application code · **–** = refused.
The active [Phase 5 packaging and releases](#phase-5-packaging-and-releases) scope is complete; Wave 2 feature work can follow.
*Superseded 2026-10-24: Waves 2 and 3 are built and merged to `main`.*
*Superseded 2026-10-26: Wave 4 is merged into `main`; all 38 packages are in the private feed at
`0.1.0-preview.0.28` from `main` `4e9905c9`, by a one-time local publication without hosted CI (`docs/private-packages.md`, "Local publication").*
*Superseded 2026-10-26: the same 38 originals are on nuget.org, published by hand without a workflow; the
repository stays private (`docs/private-packages.md`, "nuget.org publication").*
Wave 2 is the first follow-on Capability set after the merged Wave 1; Wave 3 needs Wave 2 pieces;
Wave 4 is the sample agent and the template's third composition.

| Capability | Form | Package or home | Wave | Adopted from (`repo@commit path:line`) | What the Kit adds | Proof |
|---|---|---|---|---|---|---|
| Paging | R | `Cqrs`, `Persistence.Dapper`, `Persistence.EFCore`, `Web` | 2 | see I.2.1 | page types, allow-listed sort, one HTTP page shape (*built 2026-10-07, with allow-listed filters added: a `PageDeclaration` beside the query names its sort keys and filter fields only, and each helper maps the names, Dapper to a column per key with each filter as the parameter `@{field}_{operator}` of the Feature's own `WHERE`, EF Core to an expression per key and a predicate per filter, instead of I.4's `SortBy<ProductDto>(x => x.Name, "name")`; the query's validator calls `PageDeclaration.Validate`, through `PouryaKit.Validation`'s rule `AllowedBy(declaration)` since 2026-10-16 (one line in place of the block each paged validator repeated, the same Errors with their targets and codes); `Persistence.Dapper` references `Cqrs` in place of `Domain` for the Page types, as section C's table records*) | paged `ListProducts` through Gateway and HTTP; unknown sort key → validation Error; page beyond end → empty page with total |
| One JSON setup | R | `Domain` (options), used by `Web` and `Messaging.Wolverine` | 2 | Wave 1 has two separate registrations (`src/PouryaKit.Web/WebServiceCollectionExtensions.cs:37`, `src/PouryaKit.Messaging.Wolverine/WolverineHostApplicationBuilderExtensions.cs:47`) | one `JsonSerializerOptions` factory for typed IDs, HTTP and messages; gRPC stays protobuf (*built 2026-10-07: `UsePouryaKitJson` in Domain, applied by Web to ASP.NET Core's HTTP options and by Messaging.Wolverine to Wolverine's, so a typed ID has the same JSON in a response and in its envelope; Cqrs and Caching apply it too, and `SharedJsonTests` keeps every other setup out*) (*amended 2026-10-07: messages are read with its strict variant `UsePouryaKitWireJson`, .NET 10's `RespectRequiredConstructorParameters` and `RespectNullableAnnotations` on and unknown members skipped, so a body that lacks a member its contract requires, or carries null for a non-nullable one, is dead-lettered instead of handled with defaults; HTTP, cache and recorded responses keep the lenient reading; ADR 0006's strict wire reading amendment*) | a typed ID round-trips identically through HTTP and an envelope; `StrictWireReadingTests` over RabbitMQ |
| CORS, response compression, rate limiting | R | `Web`, from configuration | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Web/Extensions/CompressionExtensions.cs:21-34`; `RateLimit/RateLimiterExtensions.cs:33-47` (429 through `IProblemDetailsService`, fits ADR 0008); `CloudNativeKit@e81d65e src/CloudNativeKit.Web/Cors/DependencyInjectionExtensions.cs:34-40` | all three live in the shared framework (no package); named CORS policy from configured origins, **no** `AllowAnyOrigin` fallback (rejected: `CorsExtensions.cs:47`); partition by Actor, never by `X-Forwarded-For` or Host header (rejected: `RateLimiterExtensions.cs:71-72,109-111`); `QueueLimit` configured separately (*built 2026-10-07: `AddPouryaKitWeb` registers all three, each off until the Host binds `PouryaKitCorsOptions`, `PouryaKitCompressionOptions` or `PouryaKitRateLimitOptions`, validated at start; the Host applies a named policy with `UseCors(name)`, because a minimal API endpoint's `RequireCors` answers no preflight (corrected on 2026-10-11: `RequireCors` adds no middleware, and once the CORS middleware runs it answers the endpoint's preflights too); compression is added outermost only when enabled, with `EnableForHttps`; the partition is the Actor the request's Operation runs as (an `AllowAnonymousAs` endpoint's named Actor for its unauthenticated callers, the continued Actor for a trusted peer); a rejection sets 429 and the existing status code pages write the problem response*) | 429 is a problem response; disallowed origin refused; compression on for HTTPS when enabled |
| API versioning, OpenAPI, Scalar | P | `PouryaKit.Web.OpenApi` → `Web`; `Asp.Versioning.Http` 10.2.3, `Asp.Versioning.OpenApi` 10.2.3, `Microsoft.AspNetCore.OpenApi` 10.0.13, `Scalar.AspNetCore` 2.18.3 (all MIT; *amended 2026-10-24: were 10.0.12 and 2.17.13*) | 2 | `woo@149a3cf src/Web/ApiVersioningExtensions.cs:19-49` (header + URL readers, `'v'VVV`, substitute in URL); `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Web/OpenApi/OpenApiVersioningDocumentTransformer.cs:27-38,64-111`; `CloudNativeKit@e81d65e src/CloudNativeKit.Web/Minimal/Extensions/MinimalApiExtensions.cs:59-70` | per-Module route group gets a version set; documents enumerated from the version set, never a hard-coded `["v1"]`; sunset dates from configuration, not "now + 60 days" (rejected: `VersioningExtensions.cs:33-34`, ADR 0005); security requirements per operation (rejected: Woo's transformer adds schemes only) (*built 2026-10-07, with `Scalar.AspNetCore` 2.17.14: a Module's Composition Root declares its versions with `AddPouryaKitApiVersions`, and Web's `MapPouryaKitModuleEndpoints` maps its endpoints through the `IModuleRouteGroups` the package registers, on two groups inside the Module's group bound to one version set, `v{version}` for the URL reader and the unversioned path for the `api-version` header reader; the Host registers `AddPouryaKitOpenApi` and configures each version's sunset date and link, and the document notices name that date without reading the clock*) | one document per version; Scalar serves them; deprecated version reports sunset |
| Reverse proxy | P | `PouryaKit.ReverseProxy`, its own process (*renamed 2026-10-10: the Capability and its package were named Gateway, the glossary's term for a Module's only entry point*); `Yarp.ReverseProxy` 2.3.0 (MIT; latest release targets net8, runs on .NET 10 through that asset — re-check before Wave 3; *answered 2026-10-24: re-checked on nuget.org on 2026-10-12, 2.3.0 stays the latest release and the pin*), `Microsoft.Extensions.ServiceDiscovery.Yarp` 10.11.0 (*10.10.0 until 2026-10-24*) | 3 | `cybernod@bae8ac3 src/API/Cybernod.ApiGateway.Yarp/Program.cs:40-54`; `Ultrabusiness@a85eacd src/ApiGateways/ApiGateway.Yarp/Configurations/Extensions/HostExtensions.cs:34-60` | config-loaded routes and clusters with service discovery; W3C trace context passed through (rejected: both mint a random `TraceId` header); health probe is `/alive`, which the service defaults map in every environment, not the docs UI; `/health`, which asks each database, is mapped in Development only (*restated 2026-10-10: the probe was `/health`; a readiness endpoint outside Development is decided with the Wave 4 service composition*) (*closed 2026-10-24: `/health` stays a Development endpoint; outside Development readiness is the platform's probe and liveness the recipe's `/alive`*)  *Built 2026-10-18, restating the row: `AddPouryaKitReverseProxy()` loads routes and clusters from the `ReverseProxy` section, resolves a destination that names a service by service discovery, and turns on each cluster's active probe of `/alive` unless its configuration says otherwise; YARP's defaults stay and the Kit strips no header, so the bearer token and the Kit's headers reach the Host as sent (YARP still sets `Host` and `X-Forwarded-*`), a cluster whose destinations are all out answers 503 (`HealthyAndUnknown`, fix round), and YARP's default propagator sends the proxy's own span, not the client's context passed through. The sample's `ModularMonolith.ReverseProxy` and each template's copy compose it with the service defaults.* | a route to a service resolves by discovery; `traceparent` arrives unchanged *(restated 2026-10-18: the trace id arrives unchanged and the proxy's span is the parent of the Host's request)* |
| gRPC | P | `PouryaKit.Grpc`; `Grpc.AspNetCore` 2.84.0 (Apache-2.0), `Grpc.Net.ClientFactory` 2.84.0, `Google.Protobuf` 3.36.2 (BSD-3) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Presentation/MainArchitecture.API/Configurations/Extensions/GrpcExtensions.cs:68-89`; `MainArchitecture.SharedGrpcContracts/custom_types.proto` (decimal, guid) | client factory with one interceptor that copies Actor, tenant, correlation and causation (never a bearer token: rejected `Interceptors/GrpcExceptionInterceptor.cs:48-81`); deadline from configuration; server side maps `Result` to gRPC status; protobuf contracts only; **not a loopback inside one Host** (architecture test) *(built 2026-10-18, where it differs from this row: `PouryaKit.Grpc` references Web, not the metapackage but `Grpc.AspNetCore.Server` and `Grpc.Net.ClientFactory`, as section C's table records. A Module declares a service with `AddPouryaKitGrpcService<T>()` and the Host maps every one with `MapPouryaKitGrpcServices()` on a root group of its own that requires authorization and carries Web's `BeginOperationPerRequest()`, so a trusted peer's call continues its Operation and a refusal of its headers answers `InvalidArgument`; `ToGrpcResponse` maps a failed `Result` by G1, ADR 0008's gRPC amendment; `AddPouryaKitGrpcClient<T>(name)` adds the interceptor that writes the context each call is given with `WithOperationContext` through `OperationContextHttpHeaders.Pairs`, never a bearer token, with the deadline and address from `PouryaKitGrpcClientOptions`, validated at start. The loopback rule is restated: not an architecture test, since no structure shows where a configured address points, but a startup check that fails the Host when a client's address is one of its own configured listening URLs, `app.Urls` included, which exist only on the built application; the skill's rule covers the rest, an address chosen only when the server binds)* | a cross-process call carries the Operation Context; a failed `Result` is a gRPC status with the Error |
| Core helpers | P | `PouryaKit.Core`: predicate builder, reflection helpers, synchronization-context scope — nothing else (amended 2026-10-07: `ImplementingTypes` gains `Candidates`, the candidate set a registration validates; the three-public-type cap stands) | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Core/Utils/NoSynchronizationContextScope.cs:12-27`; predicate-builder visitor `Linq/PredicateBuilder.cs:19-41` | `AndAlso`/`OrElse` (both references use bitwise `And`/`Or`: `cybernod … PredicateBuilder.cs:25,40`; `CloudNativeKit … Linq/PredicateBuilder.cs:22,32`); reflection limited to implementing-type lookup (rejected: 1,782 lines returning `dynamic`); architecture test caps the package at these three | an architecture test fails if a fourth public type appears |
| Security | P | `PouryaKit.Security` → `Cqrs` | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Security/Jwt/Extensions/AuthExtensions.cs:53-59` (authenticated default policy); `FeatureAuthorizationHandler.cs:8-31` minus the admin bypass | permission contracts; an `IAuthorizer<T>` implementation that evaluates a declared permission against the Actor — optional, registered by the application for the commands it chooses (G.6) (*since 2026-10-07: a declared permission must be registered by its Module's Composition Root, or the Host fails at build*); ASP.NET `FallbackPolicy` = authenticated at the HTTP boundary (*built 2026-10-07 in `PouryaKit.Web`, which owns the HTTP boundary*); no runtime mutation of `AuthorizationOptions` (rejected: `FeaturePolicyProvider.cs:20-24`); optional PBKDF2 password hashing, an owner scope addition (*built 2026-10-07: PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt, 32-byte key, versioned `pbkdf2$1$…` encoding with upgrade detection, platform cryptography only, chosen from the OWASP Password Storage Cheat Sheet and the .NET 10 `Rfc2898DeriveBytes.Pbkdf2` reference*) | unauthenticated HTTP → 401; a command with a declared permission and the wrong Actor → `Forbidden` Error → 403; a continuation runs without re-authorization (*since 2026-10-07: a continuation is trusted only when the Kit marks it `IsTrustedContinuation`, restoring a delivery or continuing a running authorized Operation; a context application code builds, or continues outside the Operation that ran it, is checked; `CausationId` is informational*) |
| JWT bearer | P | `PouryaKit.Security.JwtBearer` → `Security`; `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.13 (MIT; *10.0.12 until 2026-10-24*) | 2 | `cybernod@bae8ac3 … Jwt/Extensions/ClerkExtensions.cs:95-149` minus Clerk | claim normalisation into the Actor and tenant from configured claim names; audience validation never silently off (rejected: `ConfigureJwtBearerOptions.cs:44`); no provider SDK, no token issuance, no API keys, no PKCE, no baked role policies (rejected: `CloudNativeKit … Security/ApiKey/InMemoryGetApiKeyQuery.cs:15-30`; `woo … src/Jwt/Woo.Jwt.csproj` Duende) (*built 2026-10-07: `AddPouryaKitJwtBearer` over the standard `AddJwtBearer`, configured from `Authentication:Schemes:Bearer`; the Host fails at start unless audience, issuer, signature and lifetime are validated; the token keeps its claim names and the configured Actor and tenant claims are written as `ClaimTypes.NameIdentifier` and `PouryaKitClaimTypes.Tenant`, which `PouryaKit.Web` reads; a missing tenant claim stays a null tenant*) | a token from a test issuer becomes the expected Actor and tenant |
| Request idempotency | R | `Cqrs` (contract) + `Persistence.EFCore` (key table) + `Web` (header) | 2 | design only: `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.Idempotency/Middlewares/RequestIdMiddleware.cs:45-83`, key `(Id, Name)` | the key is **reserved inside the command transaction** as a row keyed by `(tenant, Actor, command name, request id)` — tenant is part of the key from the first version, a null tenant stored as a fixed sentinel so single-tenant Hosts work unchanged; a replay returns the stored outcome; a failed command stores nothing, so its rollback releases the key; a concurrent duplicate waits on the row (rejected in both references: marker written after the handler on its own connection, replay returns a default success — `Types/Commands/Behaviors/IdempotencyBehavior.cs:58-93`, `IdempotencyManager.cs:30-40`; `cybernod … Inbox/Idempotency/IdempotencyService.cs:39-66`) (*built 2026-10-07: `[Idempotent]` on a command and `OperationContext.RequestId` switch on the first optional pipeline slot of ADR 0002; the request id rides on the context because a Gateway opens the Operation's own scope, Web sets it from the `Idempotency-Key` of an endpoint that calls `RequireIdempotencyKey()` and answers 400 without one, and `Continue()` and the Outbox headers never carry it; `PouryaKit.Persistence.EFCore` keeps the key and the success value in the Kit's JSON in `pouryakit_idempotency_keys` of each Module's schema, the null tenant as the empty string, which no tenant can be since `OperationContext` refuses a blank one, with `insert … on conflict do nothing` so a duplicate waits without aborting its transaction. Retention: each key records `reserved_at` from the Host's `TimeProvider`, and one `BackgroundService` per Host, added by `AddPouryaKitEfCore`, removes every Module's keys older than `PouryaKitIdempotencyOptions.Retention` (24 hours by default, Stripe's documented key retention; `Timeout.InfiniteTimeSpan` keeps them) every `CleanupInterval` (1 hour) in batches of `BatchSize` (1000), outside every request and Unit of Work; a repeat after that runs as a new request*; *2026-10-10: the key alone decides a replay, so a repeat with the same key and another body gets the first success; matching the body is left to a later spec that defines the fingerprint, the serialization it requires of every idempotent command and the store's evolution, with a test of a retry across a deployment*) | replay of a success returns the same response and writes nothing; replay after failure re-executes; two concurrent sends produce one effect; the same Actor, command and key in two tenants are two Operations |
| Caching | P | `PouryaKit.Caching` → `Cqrs`; `Microsoft.Extensions.Caching.Hybrid` 10.11.0 (MIT); Redis through `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.13 (*were 10.10.0 and 10.0.12 until 2026-10-24*) as the application's `IDistributedCache` | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Caching/Services/HybridCachingService.cs:12-69`; `Messaging/Queries/ICacheableQuery.cs:6-37` | `ICacheableQuery` with key, duration and tags; one `HybridCache.GetOrCreateAsync` (single-flight per instance), **never a failed `Result`** (rejected: `Middlewares/CachingBehavior.cs:50-84` get-then-set caches failures); a key always names its Actor and tenant, while tags name the resource (`product:{id}`) so one invalidation reaches every Actor's entry; **a query dispatched inside an active write Unit of Work bypasses the cache** both ways, since it may see uncommitted state (`src/PouryaKit.Cqrs/Pipeline/Dispatcher.cs:204-207`, `src/PouryaKit.Persistence.EFCore/EfRepository.cs:43-46`); a command declares the tags it invalidates and the eviction runs after the outermost successful commit, never on rollback; every writer of a cached resource declares its tag (the sample: `RenameProduct` **and** `RemoveProducts` invalidate `product:{id}`) (*built 2026-10-07: `ICacheableQuery` and `IInvalidatesCache` in `PouryaKit.Caching`, which fills the dispatcher's second optional slot, `IQueryCache` in Cqrs, after the checks, for the Operation's outermost query only and with the reading guard run after serialization (ADR 0002); the key adds the query's type, the tenant, as `OperationContext.NoTenant` when null, and the Actor's kind and id; the factory refuses a failure by throwing, so `HybridCache` stores nothing; the tags of the outermost and every nested command are collected on the Operation and evicted after `IUnitOfWork.RunAsync` returned a success, a refused eviction logged; the entry is the success value in the Kit's JSON; `RemoveProducts` removes by name, so it cannot name `product:{id}` before it runs and invalidates `products`, a tag every `GetProduct` entry also carries*) | a failed query is not cached; two Actors never share a key; a nested query during a command that then rolls back leaves no entry; a warm `GetProduct` after `RemoveProducts` is `NotFound` |
| Scheduled and delayed work | R | `Messaging.Wolverine` (+ `.EFCore` for durability) | 2 | Wolverine scheduled messages (`JasperFx/wolverine@1f22835 docs/guide/messaging/message-bus.md:412-428`: durable only on a durable outbox endpoint) | two distinct forms, as G.6 and ADR 0007 require: **delayed continuation** — `Schedule(command, at)` from inside an Operation is written through its Outbox and runs with that Operation's Actor, tenant, correlation and causation (`OperationContext.Continue()`; *since 2026-10-07: it stays a trusted continuation only when its delivery restores the context through Messaging.Wolverine's `ContinuedOperationContext`*); **independent recurring work** — declared in a Module's Composition Root with a named system Actor, durable through the message store; neither takes system authority by default; no Quartz (rejected: Cybernod's in-memory Quartz store loses jobs on restart). *Built 2026-10-07: the delayed continuation is `IDelayedCommands.Schedule(command, at)` for a command its Module declared with `AddPouryaKitDelayedCommand`, written in the Outbox step and delivered on its own durable queue with a Receipt; the store fires it on the system clock, not the Operation's `TimeProvider` (ADR 0005 and ADR 0006, delayed commands amendments).* *Built 2026-10-07: the recurring form is `AddPouryaKitRecurringCommand(name, systemActor, occurrence, module)` in the Module's Composition Root, its cadence `PouryaKitRecurringCommandOptions.Every` from configuration; each occurrence runs as a new Operation of `Actor.System(systemActor)`, stores the next occurrence in its own commit through the Outbox and records a Receipt keyed by the occurrence's time, so a Host start that finds none waiting stores the first and two colliding chains apply one effect (ADR 0005 and ADR 0006, recurring commands amendments).* | a delayed command survives a restart and runs once as the scheduling Actor; a recurring declaration runs as its named system Actor; neither runs as the other |
| Observability | P | `PouryaKit.Observability` → `Cqrs` (the plan listed Messaging too; nothing from it is used since the parent is recorded on the Operation); `System.Diagnostics` only (exporters are template content) | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Core/Observability/Behaviors/ObservabilityPipelineBehavior.cs:37-74`, `CommandHandlerMetrics.cs:13-37,119` (correct `TotalSeconds`; rejected: Woo/CNK record whole seconds) | one `ActivitySource` per Operation with command/query tags and duration metrics; `traceparent`/`tracestate` written into Outbox headers and restored on delivery (if Wolverine already propagates W3C context, the Kit relies on it — the spike in I.6 checks) (*built 2026-10-07, as ADR 0009 decided: `AddPouryaKitObservability` wraps the scoped dispatcher, so one `pouryakit.operation` activity per command or query, of the source `PouryaKit`, is current around the whole Operation and its Outbox write, tagged with Module, operation, Actor kind and outcome, with a `pouryakit.operation.duration` histogram in fractional seconds; Wolverine carries `traceparent` as the envelope's `parent-id`, the Outbox adds the `pouryakit-tracestate` header, and every delivery records both on the `Operation` (`Operation.TraceParent`), so its activity's parent is Wolverine's span when one is sampled and the publishing Operation otherwise; local proof only; RabbitMQ follows in the next note*) (*proven across RabbitMQ 2026-10-07, ADR 0009's trace across RabbitMQ amendment: `parent-id` and `pouryakit-tracestate` cross the broker, and the receiving Host's fan-out writes the broker envelope's `parent-id` into each copy's `pouryakit-traceparent` header, read when the copy names no parent of its own, because Wolverine parents each copy on the activity current at the fan-out*) | a delivery's activity is a child of the sending Operation's trace |
| Health, resilience, service discovery, Serilog | T | Aspire ServiceDefaults template | 2 | Aspire 13.6 template | none; liveness checks no external dependency (*built 2026-10-07, as the Proving Sample's `ModularMonolith.ServiceDefaults`, which `Inventory.Host` composes: the Aspire 13.6 shape with the `PouryaKit` source and meter added to OpenTelemetry, OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, a `databases` readiness check beside the `live` self check, `AddPouryaKitResilience()` in place of `AddStandardResilienceHandler()` in `ConfigureHttpClientDefaults` so every client keeps one pipeline, and an opt-in Serilog recipe; no Kit or Aspire package*) | template compiles in all three compositions (*two in Wave 2: the monolith and the modular monolith, each generated, built and smoke-tested with the recipes by the package validation path; the service composition is Wave 4*) |
| HttpClient context handler | R | `Web` (or `Security` if it needs the Actor type only; *built 2026-10-07 in `PouryaKit.Web`, which owns both ends of the HTTP boundary, with the standard resilience handler*) | 2 | shape: `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Security/Utilities/HttpClientAuthorizationDelegatingHandler.cs:14-33` | a `DelegatingHandler` that copies Actor, tenant, correlation and causation headers onto an outgoing call; **never the caller's bearer token** (G.6; rejected `:23-30`); the receiving Host's `Web` reads the same headers only from a configured trusted peer; outgoing HTTP retry without Aspire, an owner scope addition (*built 2026-10-07: `AddPouryaKitResilience(replay)` adds the standard resilience handler with no Aspire package or ServiceDefaults registration, and retries only GET, HEAD, OPTIONS and TRACE unless a client opts in with `HttpReplay.AllRequests`*) | headers present on the outgoing request; a token is never forwarded |
| Integration Event versioning | R | `Messaging` rule, enforced at startup | 2 | Wave 1 `[WireName]` convention | one wire name carries one version (`catalog.product-created.v1`); a new version is a new wire name and a new contract type; both may be published for a transition; startup fails on two types with one wire name (*built 2026-10-07: the declaration in the Composition Root refuses a second type under one wire name and one type under two, and the Host's start refuses a type Wolverine would also route to another subscribed type's queues, or wherever another published type goes; ADR 0006's Integration Event versions amendment*) | a refusal before the Host serves; a subscription to v1 does not receive v2 |
| RabbitMQ transport | P | `PouryaKit.Messaging.Wolverine.RabbitMq` → `Messaging.Wolverine`; `WolverineFx.RabbitMQ` 6.52.0 (MIT; *amended 2026-10-09: was 6.46.0; 6.48.0 and later rebuild every listener after a connection recovery, `JasperFx/wolverine` issues 4864 and 4867*; *amended 2026-10-09 follow-up: was 6.48.2; 6.48.3 changes no file of the RabbitMQ transport*; *amended 2026-10-24: was 6.48.3; ADR 0004's WolverineFx 6.52.0 amendment*) | 2 | `woo@149a3cf src/Wolverine/Extensions.cs:64-88` (connection, auto-provision); `CloudNativeKit@e81d65e src/CloudNativeKit.Integration.Wolverine/WolverineMessageTopologyExtensions.cs:35-75` (exchange per message, queue per listener, `UseDurableInbox`) minus its static registries | per-subscription queue naming so two services never compete for one queue; durable inbox on every listener (conventional routing creates listeners without it: `docs/guide/messaging/transports/rabbitmq/conventional-routing.md:28-34`); same Receipts and headers as local delivery | **spike first** (I.6): one RabbitMQ queue per subscription versus receive-once-then-local-queues under ADR 0006 (*built 2026-10-07, as ADR 0006's RabbitMQ topology amendment decided, topology B: `UsePouryaKitRabbitMq(connectionName, serviceName)` routes every publication to a fanout exchange named by its wire name with a durable outbox, so a publication whose only subscriptions are in other Hosts is written in its commit, and gives each Host one durable queue named by its required service name, with a durable Inbox, bound only to the wire names it receives from other Hosts (`Receive`, `ReceiveSnapshot` in its composition); the Kit's pre-built handler fans each received envelope out to the subscriptions' local queues with every `pouryakit-` header, so Receipts, retries, dead letters and the Operation Context work as inside one Host and `MessageIdentity.IdOnly` stays; no broker dead-letter queue, and the Host refuses to start with a broker listener that is not durable or dead-letters on the broker; a message published before a receiving Host first declared its queue is lost to it, so it is started first (deployment rule); the Proving Sample's `Inventory.Host` is the broker proof Host, and `RabbitMqDeliveryTests` proves every invariant against RabbitMQ and PostgreSQL, one test with that Host as an OS process; cross-process tracing is in the Observability row*; *amended 2026-10-07: a wire, subscription or service name beginning with `amq.`, which RabbitMQ refuses with 403, is refused at registration, and service-name uniqueness is a deployment rule no check can enforce*) |
| Multi-tenancy | R (note, not a package) | `Cqrs` (tenant already on `OperationContext`), `Persistence.EFCore` (global filter), `Messaging` (header exists), `Security.JwtBearer` (tenant claim) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Infrastructure/Identity/InternalRequestContext.cs:13-34`; `BuildingBlocks.Persistence/Extensions/EntityFrameworkCoreModelBuilderExtensions.cs:33-55` (composed filters) | tenant set only from a verified claim, never from a client header (rejected: `Middlewares/OrganizationInformationMiddleware.cs`); EF query filter from `OperationContext.Tenant` **and** a tenant check in the prepare step (a tracked aggregate of another tenant fails the save); the Dapper lease exposes the tenant as a parameter and every read and count in a tenant-scoped Module carries `tenant_id = @Tenant` — an architecture test fails a Dapper query in such a Module without it; projection tables are keyed by `(tenant_id, product_id)` and Integration Event and Snapshot handlers apply under the delivered tenant; Receipt key gains the tenant; a Stream uses Marten conjoined tenancy with the same tenant id; an agent tool inherits the tenant through the Operation Context it runs under (*built 2026-10-16, changing this row: `ITenantScoped` in `Domain` marks an aggregate, which sets its own tenant from the Operation Context its handler reads, never the Kit; `PouryaKitDbContext.TenantFilter`, a named query filter the model-finalizing convention adds beside the soft-delete filter on every tenant-scoped root entity, compares against the Operation's tenant read through the DbContext's application services each time a query runs, so a DbContext built where no Operation began still builds and reads no tenant's rows, and a cross-tenant read names the filter in `IgnoreQueryFilters`; `AddPouryaKitTenancy` in `Cqrs`, on a Module's builder or on the Host's own services for an application without Modules, registers a `Tenant.Required` authorizer for every command and query of the Module and records the declaration Host-locally, read through `HostComposition.IsTenantScoped`; the authorizer runs for trusted continuations too, a deliberate exception to G.6, recorded in ADR 0007's tenant requirement amendment; Catalog is tenant-scoped in the Proving Sample, its Dapper reads state `tenant = @Tenant`, and its storefront list is authenticated, since an anonymous caller has no tenant. The Receipt and Snapshot version keys stay as built; the Dapper structure rule comes later. Built 2026-10-17: the EF Unit of Work's prepare step refuses with `TenantErrors.Mismatch` a save that adds an `ITenantScoped` entity for another tenant, or changes or removes one, or one a change is attributed to, loaded under another tenant or whose tenant changed, read before a removal becomes a mark; the delivery step refuses, before its Receipt, a Notification, Integration Event or Snapshot with no tenant into a tenant-scoped Module, which is retried and dead-lettered naming `Tenant.Required`; `RecurringCommandTenancyCheck` and `SnapshotKeyTenancyCheck` fail the build for a recurring command, and for a Snapshot of an aggregate whose key neither includes the tenant nor is a Guid, in such a Module (ADR 0006's tenants at delivery and in the keys amendment); Inventory keys its copies of Catalog's products by tenant and product, under the delivered tenant; built 2026-10-17, with no runtime seam: `DapperTenantTests` holds every SQL literal of a Module the Host records as tenant-scoped that reads a tenant-scoped table to one `SELECT` with no set operator, subquery or top-level `OR`, whose `WHERE` holds each such table to `@Tenant` with a top-level conjunct of its own; the column is `tenant`, and the lease is unchanged: a Feature binds the injected `OperationContext`'s tenant as `@Tenant` itself; 2026-10-17: the prepare step refuses with `Tenant.NotLoaded` a change to a tenant-scoped aggregate or part that no query loaded, so an attached entity is refused before the save, and the model convention makes each tenant-scoped root's `Tenant` a concurrency token beside `Version`, and each part's key to its holder one too, the guard for a write that reaches the database; 2026-10-17: each such part carries `TenantFilter` too, passing only where its holder is read, so a query of its own set reads no other tenant's part; 2026-10-17, amending the "no runtime seam" above by the owner's decision, since the structure rule proves the predicate's shape but not the value a handler binds: `IReadConnectionFactory.OpenAsync` sets the PostgreSQL session setting `pouryakit.tenant` on each lease, with a parameterised `set_config`, to the tenant of the scope's Operation read when the lease opens, or to the empty string, which matches no row, where it has none; the factory is per scope over the Host's one data source; a tenant-scoped read compares `tenant = current_setting('pouryakit.tenant')`, the one conjunct `DapperTenantTests` admits, so a handler's own `@Tenant` fails; Npgsql resets a pooled connection's session state, and every lease sets its own tenant either way*) | a tenant cannot read (Gateway, HTTP, Dapper), write or receive (Snapshot delivery) another tenant's rows or Receipts; a cross-tenant aggregate load fails at save |
| Event-sourced write model | P | `PouryaKit.Persistence.Marten` → `Cqrs`, `Messaging`; Marten 9.53.0; plus `WolverineFx.Marten` 6.52.0 in `Messaging.Wolverine.Marten` (*amended 2026-10-16: was 6.46.0*; *amended 2026-10-24: were 9.46.0 and 6.48.3*) | 3 | names only from `woo@149a3cf src/EventStoreDB/Repository/EventStoreDBRepository.cs:7-16` (`Find`/`Add`/`Update(expectedVersion)`); `ExpectedStreamVersion` `NoStream`/`Any` from `CloudNativeKit@e81d65e src/CloudNativeKit.Abstractions/Persistence/EventStore/ExpectedStreamVersion.cs:3-7` | see I.4 *(spiked 2026-10-16: pinned to Marten 9.46.0 and WolverineFx.Marten 6.48.3, both MIT, an owner decision; ADR 0011 records each item's outcome and the invariants, corrects I.4's internal `Apply` methods, which Marten 9's source generator requires public, records that a Stream's id is a Guid, which a typed ID wraps, and rejects I.4's Receipts, Snapshot versions and publication record as Marten documents: they are rows of the Module schema's tables, written on the Marten Unit of Work's transaction)* *(prefactor built 2026-10-18: Receipts and the idempotency store are selected per Module through Cqrs's `IModuleServices.RouteByModule`; the store-neutral Outbox write and message store registration are `Messaging.Wolverine`'s `OutboxWriter` and `UsePouryaKitMessageStore`, and `PouryaKitIdempotencyOptions` is Cqrs's; under section C the PostgreSQL persistence call and the store database check stay in `Messaging.Wolverine.EFCore`)* *(built 2026-10-18: `PouryaKit.Persistence.Marten` → `Cqrs`, `Persistence`, with `Marten` and an `Npgsql` floor at the Kit's line, not `Messaging`, since its Outbox step is the seam `IStreamOutbox`; `PouryaKit.Messaging.Wolverine.Marten` → `Messaging.Wolverine`, `Persistence.Marten`. A Module calls `AddPouryaKitStreams(schema)`, the Host `UsePouryaKitMarten(connectionName)`; the Marten Unit of Work begins its transaction before the command, appends with `FetchForWriting` at the version read, commits through the Marten outbox or itself when Marten saves no work, and logs a send that fails after the commit; Receipts, Snapshot versions, publication records and keys are rows of the Module schema's tables with the EF statements; ADR 0011's packages as built amendment records the decisions and `StreamTests` the invariants)* | see I.4 spike and acceptance |
| AI | P | `PouryaKit.AI` → `Cqrs`, `Modularity`; vector store in `PouryaKit.AI.Postgres` | 3 (after scheduling) | `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.AI` (pattern) | see I.4 *Built in part 2026-10-18, where I.4 differs: one chat client for OpenAI, Azure OpenAI through its v1 endpoint and Ollama through its OpenAI-compatible API, all on the `OpenAI` client; `OllamaSharp`, which I.4 named for Ollama, is dropped because it declares a licence file and no expression, and so is I.4's resilience handler on the provider's client, as the 2026-10-10 note in I.6 asks: the named client adds none and the OpenAI client's own retries are off, so a Host keeps its one pipeline. `AddPouryaKitAI()` builds the Host's one `IChatClient` from `PouryaKitAIOptions`, validated at start, with the allow-list (`Tools`) and the iteration cap (`MaximumIterationsPerRun`) applied to every run; the Gateway tool, `Tool` on a Module's `IGatewayProcessor<TModule>`, runs each call through the Gateway under the run's `Continue()`, trusted only inside the authorized Operation the run executes in, and returns a failed `Result` as a failed tool result. Proven against an in-process OpenAI-compatible stub and once against Ollama in a container. Agent runs, sessions, workflows, memory, MCP, embeddings and the Ollama resource follow.* *Built in part 2026-10-18: I.4's agent capabilities on the Microsoft Agent Framework (`Microsoft.Agents.AI` and `.Workflows` 1.26.0, 1.25.0 until 2026-10-24), as the owner's 2026-10-06 answer 3 decided, over the Host's one client, whose function invocation an agent keeps, so the allow-list and the cap hold: plain, streaming and structured runs; sessions through `IAgentSessionStore`, a port the application implements, the Kit keeping none; Microsoft.Extensions.AI's counting and summarising reducers in the agent's history; memory as an `AIContextProvider` over the application's store; sequential, concurrent and handoff workflows, whose handoff declarations the allow-list passes since the client never invokes them; and `IChatGuardrail`, the application's check before and after each call to the model, with no policy of the Kit's. Scheduled agent work is a recurring command whose handler runs the agent, under its system Actor. I.4's `Microsoft.Agents.AI.OpenAI` is not needed, the agent running over the Host's `IChatClient`. MCP, embeddings and the Ollama resource follow.* *Built in part 2026-10-18: I.4's MCP client from configuration, stdio and HTTP, on the official SDK's client package `ModelContextProtocol.Core` 2.3.0 (Apache-2.0; 2.2.0 until 2026-10-24) rather than the `ModelContextProtocol` 2.2.0 I.4 names, whose hosting and dependency-injection extensions serve an MCP server, not a client: `PouryaKitAIOptions.McpServers` names each server, validated at start; `IMcpServers`, a singleton of the Host's services, connects each at its first listing and is disposed with them, ending a stdio server's process and an HTTP server's session, so no run owns a client; its tools, `McpClientTool`s, are functions a run is given beside its Gateway tools, under the one allow-list and the one cap. A stdio server inherits only the variables a process needs to start and those its options name. Embeddings and the Ollama resource follow.* *Built in part 2026-10-18: embeddings and semantic search over pgvector. `AddPouryaKitEmbeddings()` builds the Host's `IEmbeddingGenerator<string, Embedding<float>>` on the chat client's one OpenAI client from the same `PouryaKitAIOptions`, its `EmbeddingModel` validated at start, for all three providers. `PouryaKit.AI.Postgres` (→ AI, Persistence.Postgres; section C's row) gives a Module `IVectorStore`, which stores text by its embedding on the Unit of Work's transaction and finds the nearest by cosine distance, both within the Operation's tenant, in tables of the package's own schema `ai` that its migration helper `CreateVectorCollection` creates with the `vector` extension, or fails naming the fix. I.4's `CommunityToolkit.VectorData.PgVector` 1.0.1 passes the licence test and targets .NET 10, and is still not used: its collection commits on a connection of its own, outside the Unit of Work (ADR 0003), its tables come from a run-time call rather than a migration, and its tenant filter is the caller's; neither is the `Pgvector` client, the embedding going as a `real[]`.* | see I.4 |
| Realtime | P (optional) | `PouryaKit.Realtime` → `Messaging` | 3 | endpoint shape from `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.SSE/Extensions/SseEndpointExtensions.cs:58` (`TypedResults.ServerSentEvents`); reject `Services/SseConnectionManager.cs:22` and singleton registration in `Extensions/SseServiceExtensions.cs:31`; also reject `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.SignalR/Implementations/SignalRConnectionManager.cs:23` | one SSE browser stream; a Notification Handler pushes after commit; Feature code never holds connections; a client connected to any instance receives the event; a connection list that lives only in one process is forbidden, as both reference managers lose delivery across instances; no SignalR package or second push channel; CloudNativeKit and Woo supply neither channel *Built 2026-10-18: `PouryaKit.Realtime` references Web, Persistence.EFCore and Npgsql, the build stands over this row's `→ Messaging`. The mechanism is ADR 0012: `IRealtimePublisher` writes `pg_notify` on the delivery's own transaction, so PostgreSQL delivers it to every instance's one listener only when that transaction commits; `MapPouryaKitRealtime` serves `TypedResults.ServerSentEvents` from the instance's clients keyed by tenant and Channel, admitting the Channels the endpoint's rule allows for the Actor; a payload of 8000 bytes or more fails the push with `Realtime.PayloadTooLarge`; a reconnected listener sends every client a gap event. Delivery to clients is at most once and not durable.* | a Notification committed on one instance reaches a client connected to another instance through SSE; rollback emits no event |
| File storage | P | `PouryaKit.Storage` (contract) + `.S3` / `.AzureBlob` providers | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.FileManagement/Contracts/IFileStorageProvider.cs:18-59` (contract shape); `cybernod@bae8ac3 … FileUpload/Extensions/AzureStorageExtensions.cs:22-40` | one contract, one provider per package (`AWSSDK.S3` Apache-2.0; `Azure.Storage.Blobs` 12.30.0 MIT) *Built 2026-10-17, the contract and the S3 provider: `IFileStorage` in `PouryaKit.Storage` (references Domain only) uploads, downloads and deletes a file under a key the caller chooses and signs a time-limited link, each call with a `CancellationToken` and returning a `Result`; a missing file is `StorageErrors.FileNotFound`, a not-found Error, a delete of a missing file succeeds, and a store failure throws. `PouryaKit.Storage.S3` on `AWSSDK.S3` 4.0.104.6 (4.0.104.4 until 2026-10-24) registers it with `AddPouryaKitS3Storage()`, from `PouryaKitS3StorageOptions` the Host binds from configuration, validated when the Host starts and read in the registered factory. A Host composes exactly one provider: the provider's composition check fails the Host's build when a second `IFileStorage` is registered, before or after, naming each registration (2026-10-17; until then a second registration threw, and only after the first). A link's lifetime is bounded by what the provider signs, S3's seven days, with `StorageErrors.LinkLifetimeOutOfRange`. The Kit guarantees the contract and the provider, not a transactional file: the skill states store-then-record and the application's retention of orphans. Azure Blob follows.* *Built 2026-10-18, the Azure Blob provider: `PouryaKit.Storage.AzureBlob` on `Azure.Storage.Blobs` 12.30.1 (the row's 12.30.0 had a patch release) registers it with `AddPouryaKitAzureBlobStorage()`, from `PouryaKitAzureBlobStorageOptions` (a connection string with its account key, or a service URL with account name and key) the Host binds from configuration, validated at start and read in the factory. A link is a read-only service SAS signed with the account key, bounded at seven days by the Kit, since Azure bounds a key-signed SAS by nothing of its own. The one-provider check moved to `PouryaKit.Storage`, which now references Cqrs, and both providers register through `AddPouryaKitFileStorage`, so S3 and Azure Blob together fail the Host's build in either order, naming both: the "fails at start" is the build-time check. Proven against Azurite 3.37.0 in Testcontainers, with `--skipApiVersionCheck`, since no Azurite release accepts the SDK's service version 2026-10-06.* | upload/download/presigned URL per provider in a container test |
| Email | P | `PouryaKit.Email` + `.MailKit` (`MailKit` 4.18.2 MIT; 4.18.1 until 2026-10-24) | 3 | `CloudNativeKit@e81d65e src/CloudNativeKit.Email/IEmailSender.cs`, `MailKitEmailSender.cs:29-34` | `CancellationToken` on every call; a client per registration, not per send (rejected: `SendGridEmailSender.cs:22`); sent from a Notification Handler *(built 2026-10-17: `IEmailSender.SendAsync(EmailMessage, CancellationToken)` returns a `Result` in `PouryaKit.Email`, which references Domain alone; `AddPouryaKitMailKit()` in `PouryaKit.Email.MailKit` registers the Host's one sender, a singleton over `PouryaKitMailKitOptions`, which the Host binds from configuration and the Kit validates at start. The choice was between one connected client guarded for one send at a time and a connection per send from one registration; the Kit chose a connection per send from one registration, as MailKit's own guidance allows: `SmtpClient` documents reusing a connection as an optimization for many messages over a short period and asks for a lock to share one between threads, while a Notification Handler's sends are sporadic and concurrent, and an idle held connection is dropped by the server. "A client per registration" therefore holds for the sender and its options; the rejected shape, upstream's client built per send from configuration read per call, is not taken. G.2's guarantee is proven through a Papercut container: a rolled-back Operation sends no mail, and a mail whose delivery fails after the send arrives twice, the stated limit; since 2026-10-17, the provider's composition check fails the Host's build when a second `IEmailSender` is registered, before or after, naming each registration)* | a Notification Handler sends through a test SMTP container |
| Saga | P (optional) | `PouryaKit.Messaging.Wolverine.Sagas` | 3 | Wolverine sagas (`docs/guide/durability/sagas.md:11-20,556-604,617-630`) | native Wolverine `Saga` with PostgreSQL storage and `TimeoutMessage`; approved 2026-10-06 as the one documented exception to ADR 0001/0006 (a public, Wolverine-discovered handler type) and a fourth, optional kind of reaction beside G.2 *(spiked 2026-10-18: on WolverineFx 6.48.3, MIT, the Kit's line; ADR 0013 records the exception's extent and the invariants: a step runs on Wolverine's generated chain outside every Operation, reached through a Kit route to the saga's own durable queue as a Kit input type per saga and event, since the Kit's pre-built handler of a subscribed type would shadow the saga's chain; each step is claimed by saga, input wire name and event identity in its own transaction; the saga's identity is the event's tenant with its business id, each part escaped; emissions go through a Kit adapter; state is Wolverine's lightweight saga table in the Kit's message store schema; Wolverine's own logical deduplication is rejected, as it claims outside the step's transaction)* *(built 2026-10-18: `PouryaKit.Messaging.Wolverine.Sagas`, references Messaging.Wolverine and `WolverineFx.Postgresql`; `AddPouryaKitSaga<TSaga>(name, systemActor, …)` on a Module's builder, `PouryaKitSaga`, `SagaInput<TSaga, TEvent>` and `SagaTimeout`, through which a saga's Module names no Wolverine type; the claims table is created with the message store as the Host starts, under its migration lock, as ADR 0013's invariant asks, not by a Module's migration, since the message store is the Host's and a saga's Module may have no database of its own; an Integration Event from another Host reaches a saga through the broker fan-out; a saga's state table and its inputs' stored names derive from its declared name, not Wolverine's simple type names; the service location policy is a build-time composition check; the Proving Sample gains no saga, and package validation publishes the test Modules' Host in Release with its generated chains)* | a saga started by one Integration Event, advanced by a second, and timed out by `TimeoutMessage`, each step through the outbox |
| Number sequences | R | `Persistence.Postgres` (optional, only a reusable sequence) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Persistence/Numbering/NumberSequenceAllocator.cs:11-47` (runs on the current transaction) | PostgreSQL `SELECT … FOR UPDATE` on a sequence row inside the Unit of Work; gap-free per scope key; `23505` mapped to `Conflict` *Built 2026-10-16, changing the mechanism: `AddPouryaKitNumberSequences()` on the Module's builder, `INumberSequences.NextAsync(scopeKey)` returning a `Result<long>`, and `CreateNumberSequenceTable(schema)` for the table in the Module's schema, named at first use when missing. The allocation runs on the Unit of Work's transaction as an insert of the scope row that does nothing on conflict, then an update that increments and returns, whose row lock serialises Operations on one scope key until commit or rollback; a deadlock aborted while `NextAsync` waits is `UnitOfWorkErrors.ConcurrencyConflict` (one aborted in another write stays an exception). The row's `Conflict` question dissolved (2026-10-16): the allocator raises no unique violation, so nothing of it maps `23505`; a unique violation on the business column that holds the number is the existing EF-save mapping (`IUniqueConstraintReader`, `MapPouryaKitUniqueConstraint`) to the Module's Error, and a plain-SQL write of the number stays outside it, as the 2026-10-10 note says.* | two concurrent Operations get consecutive numbers; a rolled-back Operation leaves no gap |
| Reporting and export | P (optional renderer) + opt-in Module | `PouryaKit.Reporting` renderer; one report Module using `Cqrs`, `Persistence.EFCore`, `Messaging.Wolverine` scheduling and `Storage` | 3 | `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.Reporting/Extensions/ReportingExtensions.cs:80-122`; `src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Infrastructure/Reporting/Extensions/ReportingInfrastructureExtensions.cs:38-41`; `src/BuildingBlocks/MainArchitecture/Presentation/MainArchitecture.API/Configurations/Endpoints/Reporting/ReportEndpointExtensions.cs:25,45-46,100-103,173`; claim fencing pattern from `Ultrabusiness@a85eacd … MainArchitecture.Domain/Aggregates/ReportJobAggregate/Entities/ReportJob.cs:294-313,332-421,478-481` | the optional renderer provides layout primitives, fonts, branding, PDF, CSV and spreadsheets; concrete layouts stay in the adopting Module; the Host registers the report Module once; `POST /reports` dispatches a command returning a `Result` and HTTP 202 with a job id, with no report-list endpoint; the Module stores the job, generates the file in the background, keeps a retention job and tells the caller where the finished stored file is; generation and retention use the Kit's scheduler, rejecting Ultrabusiness's Quartz `ReportGenerationJob` and `ReportRetentionJob`; files use the Kit's storage contract, with no second file library; no MediatR; Ultrabusiness sets QuestPDF to `LicenseType.Community`, so QuestPDF is not selected under ADR 0002; the PDF library remains unnamed and its permissive-licence check is open; claim-and-settle uses the Kit's `Version` and attempt fencing *Built 2026-10-18, the renderer; the report Module follows. The libraries this row left open are named: `PouryaKit.Reporting`, referencing Domain alone, renders a Module's `ReportLayout` (a title, headings and paragraphs, one table of text cells, a logo and an accent colour, a page header and footer) through `IReportRenderer.Render` into a `RenderedReport` with its bytes, content type and file extension, or refuses it with `ReportErrors.TableShape` or `ReportErrors.LogoFormat`. PDF through MigraDoc and PDFsharp 6.2.4, the last MIT release (6.5.0 is AGPL-3.0-only), with Roboto 2.138 (Apache-2.0) embedded, so a PDF renders with no system font; CSV through CsvHelper, UTF-8 with a byte order mark and formula injection escaped; a spreadsheet through the Open XML SDK, every cell an inline string. The renderer stores nothing: the caller stores the bytes through `IFileStorage`. Built 2026-10-18, the report Module, where this row differs: `PouryaKit.Reporting.Module` is a Module the Host adds once with `modules.Add<ReportModule>()`, in its own schema `reports`; it references Modularity, Persistence.Postgres, Persistence.Dapper, Messaging.Wolverine.EFCore, Validation, Security, Web, Reporting and Storage (this row named Cqrs, Persistence.EFCore, Messaging.Wolverine and Storage; the Module stack and the job read take the rest). `POST /reports` answers 202 and the job's id once the Host's `IReportRequestAuthorizer` allowed the request, before any job is stored; the Host's `IReportLayouts` lays each kind out. Generation is a delayed command under the request's continuation, the claim committed on its own, attempt numbers and the aggregate's `Version` fencing every claim, settlement and expiry; the first claim schedules the one takeover of a stale claim; each attempt stores its file under its own key, and only the fenced settlement publishes it. `GET /reports/{jobId}` answers state and a presigned link to the requester within the job's tenant, or an Actor granted `reports.jobs.read-tenant`, and not found to anyone else. Retention is the recurring command `reports.retention` of a system Actor, across tenants, that deletes a job's files only after a committed expiry. The connection string is `app`, the Module's own, since a Composition Root receives no configuration.* | `POST /reports` returns 202 and a job id; the background job renders and stores the file and reports its location; retention removes expired output through Storage; a stale claim is taken over once and a late settle is refused; the runtime dependency closure passes ADR 0002 before the PDF library is added |
| Localization | P (optional) | `PouryaKit.Localization` | 3 | `Ultrabusiness@d685de6 src/BuildingBlocks/BuildingBlocks.Core/Localization/LocalizationExtensions.cs:15-19` (`AddLocalizationServices`); `src/BuildingBlocks/BuildingBlocks.Web/Extensions/LocalizationExtensions.cs:18-31` (`UseLocalization`) | one opt-in package supports English and Persian; an English-only Host does not reference it; no copy of Ultrabusiness's resource designer *Built 2026-10-17: `AddPouryaKitLocalization()` on a Module's builder, or on the Host's own services naming the Feature assemblies, records them through the Module seam and registers the shared framework's `IStringLocalizer<T>`, which reads each Feature's `.resx` beside its type (`.fa.resx` for Persian; no designer or generator). The request culture comes from the `Accept-Language` header alone, English or Persian with English the default, set by a startup filter, so the Host adds no middleware; the Kit's own Error messages stay English. No NuGet package: the ASP.NET Core framework reference only.* | the same localized value resolves in English and Persian; an English-only Host runs without the package |
| Aspire resources | P | `PouryaKit.Aspire`, AppHost-only; `Aspire.Hosting.PostgreSQL`/`.Redis`/`.RabbitMQ` 13.6.0 (all MIT); Ollama as a plain pinned container, `CommunityToolkit.Aspire.Hosting.Ollama` rejected since it brings `OllamaSharp`, which declares a licence file and no expression | 3 | `CloudNativeKit@e81d65e src/CloudNativeKit.AspireIntegrations/Postgres/PostgresBuilderExtensions.cs:93-189` (pinned images, volumes) | PostgreSQL, Redis when caching is registered, RabbitMQ when that transport is registered, Ollama when an agent runs locally; pinned images; no Jaeger, Zipkin, Tempo, Loki, Kibana, Prometheus, Grafana, Elasticsearch or Swagger resource *Built 2026-10-18, on Aspire 13.6.1 (this row's 13.6.0 predates it; 13.6.4 since 2026-10-24): `AddPouryaKitPostgres`, `AddPouryaKitRedis` and `AddPouryaKitRabbitMq` on an AppHost's `IDistributedApplicationBuilder` add each container on a pinned image, the tag the Kit's Testcontainers fixtures run (`postgres:18.6`, `redis:8.10.1`, `rabbitmq:4.3.6`, no management plugin), with its data in a named volume. The AppHost cannot see a Host's registrations, so its author calls Redis and RabbitMQ only for a Host whose composition caches or sends over that transport, and references each resource under the connection name the Host reads. The Proving Sample's `ModularMonolith.AppHost` runs `ModularMonolith.Host` on PostgreSQL, Redis and RabbitMQ and `Inventory.Host` on PostgreSQL and RabbitMQ; `AppHostTests` starts it headless with the dashboard off. Since 2026-10-24 it also runs `ModularMonolith.ReverseProxy` in front of both Hosts and routes Inventory's paths to `Inventory.Host` by service discovery, with the probe on. Ollama built 2026-10-18: `AddPouryaKitOllama(name, model)` is a plain container on `docker.io/ollama/ollama:0.40.6` with a named volume, not `CommunityToolkit.Aspire.Hosting.Ollama`, whose 13.6.0 brings `OllamaSharp` 5.4.12 and its licence file, which the Kit refuses; its health check pulls the model, so a Host that waits for it starts with the model served. The sample AppHost adds it only with `LocalAgent=true` and points `ModularMonolith.Host`'s `PouryaKit:AI` at it.* | AppHost starts the Proving Sample with each resource set |
| Project template | T | `dotnet new` template generated from the Proving Sample | 2 (monolith + modular monolith), 4 (service composition) | — | three compositions: the same business code, generated Composition Root and endpoint adapter per composition *Built 2026-10-07: `PouryaKit.Templates` holds `pouryakit-monolith` and `pouryakit-modular-monolith`; the business code is packed from the Proving Sample when the package is packed, never copied. The monolith is Catalog, one Module in all but name, with a plain registration (`CatalogModule.AddCatalog()`), `IDispatcher` endpoints and no `PouryaKit.Modularity`; it calls Cqrs's `AddPouryaKitCompositionChecks()`, the entry `AddPouryaKitModules` calls, whose checks run when the container is built (since 2026-10-07; until then Cqrs's `EnsurePouryaKitDeclaredAuthorizers()` through `ConfigureContainer`). Both Hosts compose every Wave 2 Capability that applies and the service defaults recipes; the package validation path generates, builds and runs both smoke tests. The service composition stays Wave 4 (I.7). Built 2026-10-07: every Host ends with `RunJasperFxCommands(args)` and references `WolverineFx.RuntimeCompilation` in Debug builds only; outside Development Wolverine loads the code `codegen write` generated (`TypeLoadMode.Static`, the Kit's default), and the package validation path runs `codegen write` and checks each generated Host's Release publish carries no runtime compiler (ADR 0004).* *Since Wave 3 (recorded 2026-10-24) both also compose gRPC and Realtime and hold the Reverse proxy's project, and the modular monolith adds Ledger on Marten and the chat client of AI.* *Built 2026-10-25: `pouryakit-service`, the service composition, Inventory alone in a Host of its own with Catalog's Contracts, receiving over RabbitMQ and serving `GetStockItem`; Inventory's project moved to the shared content, whose source in the monolith leaves it out, and the modular monolith generates as before (no AppHost in any template); the package validation path generates it, runs its smoke tests against PostgreSQL and RabbitMQ and checks its Host's Release publish.* *Proven 2026-10-25: the generated modular monolith and service run as two processes on one PostgreSQL and one RabbitMQ, and a product created in the first is read as stock from the second (`scripts/packaging/composition_proof.py`, a recorded step after a kept validation); each template's Host starts under its `https` launch profile with the documented run steps and answers REST, and gRPC where mapped, on its https URL. The steps' `dotnet user-jwts create` needed an `appsettings.Development.json`, which each template's Host now generates empty.* | each composition builds; the monolith passes an HTTP smoke test without `Modularity` |
| Sieve, Gridify, filter strings; Swashbuckle, AsyncAPI; MediatR, AutoMapper, Mapster; MemoryPack or a second JSON stack; EventStoreDB, MongoDB, Marten documents, Cosmos, Azure Postgres wrappers; API keys, token issuance, Duende; general id-generator; Quartz; FusionCache beside HybridCache; a `Core` beyond three helpers | – | — | — | — | refused: a second way to do what the Kit already does, a reciprocal or commercial licence, or a provider the Kit does not use | architecture and licence tests |

### I.4 Design notes for the capabilities that add behaviour

**Paging contract.** The sort allow-list is declared next to the query (`SortBy<ProductDto>(x => x.Name, "name")`);
the handler receives a validated `PageRequest` whose sort key is one of the declared names. Dapper
gets a small helper that appends `ORDER BY <mapped column> <direction> LIMIT @Take OFFSET @Skip` to
the caller's SQL and runs the caller's `COUNT` in the same lease — the Kit never composes `WHERE`.

**Request idempotency inside the transaction.** The HTTP boundary reads an `Idempotency-Key`
header (required for POST, PUT, DELETE when the endpoint opts in) into the Operation. The Unit of
Work inserts the key row before the handler runs; a duplicate key that already has a stored
outcome returns it; a duplicate whose row is still uncommitted waits on the row lock and then sees
the outcome; a failed Operation rolls the row back. The stored outcome is the `Result` payload, not
the HTTP response. This is the behaviour both references lack (I.3).

**Event-sourced write model (Stream).** `PouryaKit.Persistence.Marten` owns a Marten Unit of Work
that mirrors the EF one: run the handler, drain Domain Events, then `FetchForWriting<T>(id, expectedVersion)`
→ append → `SaveChangesAsync` through `IMartenOutbox`, then flush (`JasperFx/marten@b2f530e docs/scenarios/command_handler_workflow.md:111-153,278-309`;
a mismatch is Marten's `ConcurrencyException` mapped to `Conflict`). Appends use `FetchForWriting`,
never the `Append(streamId, expectedVersion, …)` overload, which needs `EventAppendMode.Rich` and
fails on a new stream in Marten 9's default Quick mode (`docs/events/appending.md:210-233`). A
Stream aggregate folds with `Create`/`Apply` methods, which may be `internal` (`docs/events/projections/conventions.md:91`),
so ADR 0001 isolation holds (*amended 2026-10-16: the methods must be `public`; Marten 9's source generator takes only
public conventional methods and an `internal` type, so the aggregate type stays `internal` and ADR 0001 still holds; ADR 0011,
"Findings"*). Notifications and Snapshots for a Stream are built from the events
appended in that commit and the stream version after the append, since the EF path's
`changedAggregates` seam does not see them (`src/PouryaKit.Messaging.Wolverine.EFCore/WolverineOutbox.cs:45`).
Reads are projections: inline (same session) or async (Marten's daemon); `FetchLatest` for the
current state. Marten runs in its own schema (`events`) in the same database. Wolverine side:
`AddMarten(...).IntegrateWithWolverine(...)` registers a **second main message store**; Wolverine
merges it with the Kit's store only when database and schema match exactly, otherwise startup
throws (`JasperFx/wolverine@1f22835 src/Wolverine/Persistence/MessageStoreCollection.cs:108-167`;
`src/Persistence/Wolverine.Marten/WolverineOptionsMartenExtensions.cs:121-135,335-352`). The
**spike** (before any package code) must prove: (1) the Kit's `PersistMessagesWithPostgresql(…, "wolverine")`
and Marten's integration with `MessageStorageSchemaName = "wolverine"` start with one main store and
one durability agent; (2) an envelope published through `IMartenOutbox` commits with its `mt_events`
row and is delivered through the Kit's routed queue; (3) a version mismatch yields `Conflict` and
no envelope; (4) a duplicate `StartStream` yields no events and no envelope; (5) typed IDs
serialise in Marten's serializer; (6) crash between commit and flush recovers as in ADR 0004.
Delivery into a Stream Module: Wave 1 records a Receipt before the handler runs through
`EfReceipts`, which throws for a non-EF Unit of Work (`src/PouryaKit.Messaging.Wolverine.EFCore/EfReceipts.cs:51-55`;
`Deliver.cs:20-24`). `PouryaKit.Messaging.Wolverine.Marten` therefore supplies `MartenReceipts`
(Receipts, Snapshot versions and the publication record as Marten documents on the same session), and
`IReceipts` is selected per receiving Module by its write model instead of one global registration
(*amended 2026-10-16: not as Marten documents, which are written only at the save, so a concurrent duplicate finds no
Receipt and runs its handler; `MartenReceipts` writes rows of the Module schema's tables, the EF tables' shapes, on the session's
transaction, which the Marten Unit of Work begins before the command; ADR 0011, "Rejected options" and its invariants*).
The spike adds (7) a `Ledger` Notification Handler publishes an Integration Event and its Receipt
commits with the Stream effect; (8) a concurrent duplicate delivery to `Ledger` yields one effect; (9)
a failed `Ledger` handler rolls back its Receipt with the Stream.
Feature code never sees `IDocumentSession`; it writes a Stream aggregate, a command handler and
projections. Marten pulls `Weasel` 9.41.0 and `NetTopologySuite` (BSD-3) into the closure — all
permissive, within ADR 0002.

**AI.** `PouryaKit.AI` registers, from configuration: an `IChatClient` for OpenAI, Azure OpenAI or
Ollama with `UseFunctionInvocation().UseOpenTelemetry(sensitive data off).UseLogging()` and a
standard resilience handler on the provider HTTP client (the reference has none); `ChatClientAgent`
creation and runs — plain, streaming, structured output; sessions with a store the application
chooses (the reference keeps them in a scoped dictionary and loses them: `Services/DefaultAgentThreadService.cs:44-46,265`);
workflows (sequential, concurrent, handoff); a chat reducer (counting or summarising); memory as an
`AIContextProvider` over a store the application supplies; an MCP client from configuration (stdio
and HTTP, with owned disposal); a guardrail hook with no built-in policy; embeddings (OpenAI, Azure,
Ollama) and semantic search over pgvector. **Tool calls:** the Kit's `GatewayTool` wraps a Module
query or command as an `AIFunction`; the call runs as an Operation under the agent run's Operation
Context (the user's Actor for an interactive run, a system Actor for scheduled work), so validation,
the optional authorization hook and the Unit of Work apply, and a failed `Result` is returned to the
model as a failed tool result, never thrown. Iteration cap and the tool allow-list, one Host-wide set applied to every run, are configuration. Packages (verified 2026-10-06, all MIT unless stated): `Microsoft.Extensions.AI`
10.10.0, `.Abstractions` 10.10.1, `.OpenAI` 10.10.1 (→ `OpenAI` 2.14.0), `OllamaSharp` 5.5.0
(`Microsoft.Extensions.AI.Ollama` is deprecated in its favour), `Microsoft.Agents.AI` 1.23.0,
`.OpenAI` 1.23.0, `.Workflows` 1.23.0 (stable; `.Hosting` is prerelease and not used),
`ModelContextProtocol` 2.2.0 (Apache-2.0), `Microsoft.Extensions.VectorData.Abstractions` 10.10.0.
Vector store: `PouryaKit.AI.Postgres` uses `CommunityToolkit.VectorData.PgVector` 1.0.1 (MIT; depends
on Npgsql 10.0.3 and `Pgvector` 0.3.2, matching the Kit's pins; `Pgvector.EntityFrameworkCore` is
net8-only and not used), in its own schema, so a Host without AI takes no pgvector. **Providers (owner decision 2026-10-06):**
`Microsoft.Extensions.AI` with the `OpenAI` client for both OpenAI and Azure OpenAI (through the
Azure v1 endpoint); `Azure.AI.OpenAI` is not referenced. `OllamaSharp` for Ollama. Microsoft Agent
Framework (`Microsoft.Agents.AI*`) supplies the optional agent capabilities. Excluded
from the pattern: System One, SQL Server/SqlClient and its global vector type handler, Quartz jobs,
prompt templates, the fluent `CreateContext` wrappers, the on-the-fly tool selector (a second way to
choose tools). Scheduled agent work uses Wolverine scheduling under a system Actor. *Superseded in part 2026-10-24, for the reasons the I.3 AI row's dated notes give: of the packages named here, `OllamaSharp`
(Ollama goes through the `OpenAI` client), `Microsoft.Agents.AI.OpenAI`, `ModelContextProtocol` (the Kit takes
`ModelContextProtocol.Core`), `Microsoft.Extensions.VectorData.Abstractions` (`PouryaKit.AI.Postgres` has its own
`IVectorStore`), `CommunityToolkit.VectorData.PgVector` and the `Pgvector` client are not used, and the provider's HTTP
client takes no resilience handler of its own.*

**Saga** (approved): a Wolverine `Saga` type with PostgreSQL storage, timeouts through
`TimeoutMessage`, started and advanced by Integration Events through the Kit's routing; the saga's
own messages go out through the same outbox. *(Spiked 2026-10-18, ADR 0013: the Kit routes each
Integration Event to the saga's own durable queue as `SagaInput<TSaga, TEvent>`; a step's state, claim, emissions
and timeouts commit in one transaction of the message store, which the Kit opens, and the emissions and timeouts
go into the Module's Outbox, written by the Kit's saga storage rather than by a Unit of Work; a step has no
Receipt and no Operation Context, its claim does the Receipt's work. This answers the I.6
note of 2026-10-10 on the Saga row.)*

**Observability.** The Kit emits; the template exports. Activities: `pouryakit.operation` with
module, operation, actor kind, outcome; delivery activities link to the publishing Operation. *Resolved, ADR 0009
(2026-10-07): a child relationship, not a link; see the record for what the Kit implements.*

### I.5 What the Proving Sample gains after Wave 1 (nothing enters Wave 1)

| Wave | Addition | Shows |
|---|---|---|
| 2 | `ListProducts` paged; `Idempotency-Key` on `CreateProduct`; `GetProduct` cached with tag invalidation on `RenameProduct` and `RemoveProducts`; a delayed `ExpireQuote` continuation (runs as the quoting Actor) and a recurring `ReconcileStock` declaration (runs as a named system Actor); versioned OpenAPI with Scalar; CORS/compression/rate limiting from configuration; a JWT test issuer and one permission-declared command; a trace that spans Catalog → Inventory delivery | paging, idempotency, caching, scheduling, OpenAPI, web defaults, security, observability |
| 2 | Inventory run as its own process over RabbitMQ (the service composition), still passing the Wave 1 delivery tests | RabbitMQ transport, the template's third composition (*corrected 2026-10-10: built as `Inventory.Host`, the broker proof Host, which is not a template; the template's service composition stays Wave 4, I.7*) |
| 3 | a Stream Module `Ledger` beside Catalog: one command appends, one projection reads, one Integration Event leaves through the existing messaging | event sourcing (*built 2026-10-18: `OpenAccount` starts an account's Stream and `PostEntry` appends to it, `GetAccount` reads its fold through `IStreams`, as the Marten packages offer no projected document, and `AccountOpenedV1` reaches Billing, which opens a welcome quote; tenant-scoped, in the `ledger` schema, in the modular template and not in the monolith, whose Host holds one Unit of Work*) |
| 3 | tenant-scoped Catalog rows, Receipts and Snapshots | multi-tenancy |
| 3 | one agent with one tool, `GetProduct` through the Catalog Gateway, run as the calling Actor; Ollama in the AppHost | AI |
| 3 | an email and an SSE push from a Notification Handler, including delivery to a client on another instance; a file upload behind the storage contract | Email, Realtime and Storage |
| 3 | AppHost with PostgreSQL, Redis, RabbitMQ and Ollama | Aspire |

### I.6 Follow-up decisions and ADR amendments

Decided above and needing only an ADR amendment when built (no owner input):

- **ADR 0003 and 0004** gain: "each write model has one commit owner; the EF Unit of Work owns rows
  and commits through the EF outbox; the Marten Unit of Work owns a Stream and commits through
  Wolverine's Marten outbox; no Operation commits through both." Recorded in the Marten spike's ADR.
- **ADR 0002** gains: the fixed pipeline has two optional slots, idempotency (before the Unit of
  Work) and caching (around a query), filled by `PouryaKit.Caching` and the idempotency registration
  only; applications still register no steps. *Resolved: ADR 0002's optional idempotency and caching slots amendment (2026-10-07)
  records the two slots, and its pipeline contracts amendment (2026-10-08) what holds: public contracts the Kit's packages fill,
  which no application type implements (`PipelineContractTests`).*
- **ADR 0006** gains the RabbitMQ answer once its spike runs. *Resolved: ADR 0006's RabbitMQ topology amendment (2026-10-07),
  topology B, receive once per Host and route to each subscription's local queue.*
- Catalogue rows superseded by this section: AI (was "application by default, Wave 4"); caching
  (section B said "native, no kit package"; this section gives it a package for the Result and key
  rules); the Security row's "also covers background commands" (contradicts G.6: continuations are
  trusted and not re-authorized; the hook is optional). *Added 2026-10-10:* Multi-tenancy (was the
  package `PouryaKit.MultiTenancy`; I.3 makes it a registration across existing packages, not a package);
  "Number sequences, AI" (was Wave 4, optional packages "only if reused across projects"; I.3 puts both in
  Wave 3, number sequences as a registration in `Persistence.Postgres` and AI as the package above).

Answered by the owner on 2026-10-06:

1. **Saga:** yes — `PouryaKit.Messaging.Wolverine.Sagas`, optional, Wave 3, with the documented
   exception to ADR 0001/0006 and G.2 recorded in its ADR when built.
2. **Stream commit owner:** yes — Marten stays optional over PostgreSQL, with Kit-controlled commits
   through Wolverine's Marten outbox, subject to the spike in I.4 passing.
3. **AI providers:** `Microsoft.Extensions.AI` with the `OpenAI` client for OpenAI and Azure v1, and
   Microsoft Agent Framework for the optional agent capabilities; no `Azure.AI.OpenAI` dependency and
   no waiting condition.
4. **Realtime:** the finished Kit uses SSE through one optional `PouryaKit.Realtime` package in
   Wave 3, used from Messaging. A Notification Handler pushes after commit, and a client connected
   to any instance receives the event. Learn the `TypedResults.ServerSentEvents` endpoint shape;
   reject both references' process-local connection dictionaries. Feature code does not hold
   connections. No SignalR package and no second push channel. G.2's SSE sentence stays as it is.
   *Built 2026-10-18: `PouryaKit.Realtime` references Web and Persistence.EFCore rather than Messaging; the I.3 Realtime row's dated note and ADR 0012 record what was built.*
5. **Reporting and export:** follow Ultrabusiness's split into one optional renderer and one
   opt-in report Module that a Host registers once. The renderer covers layout primitives, fonts,
   branding, PDF, CSV and spreadsheets; concrete layouts stay in the adopting Module. The Module
   stores the report job, generates the file in the background, keeps a retention job, maps
   `POST /reports` to a command returning a `Result` and HTTP 202 with a job id, and tells the caller
   where the finished stored file is. There is no report-list endpoint. Use the Kit's scheduler
   for generation and retention and its file storage for the output; reject Quartz, a second file
   library and MediatR. Ultrabusiness uses QuestPDF under `LicenseType.Community`; QuestPDF is not
   selected. The PDF library stays unnamed until its runtime licence passes ADR 0002; that check
   remains open. This renderer and report job stay in Wave 3. *Answered 2026-10-24: `PDFsharp-MigraDoc` 6.2.4 (MIT) was chosen, passed the licence test, and the renderer is built on it.*
6. **Localization (Wave 3 addition):** optional localization supporting English and Persian through one package,
   learning from Ultrabusiness's `AddLocalizationServices` and `UseLocalization`. An English-only
   Host does not reference it; do not copy their resource designer.

Open for Wave 3 planning (recorded 2026-10-09 on the owner's word, not decided): answer 4 stands as
written. A Notification is delivered to its handler once, on one instance, so reaching a client
connected to another instance needs a delivery between instances that this plan does not name.
Wave 3's spec names that mechanism, and how it fits "no second push channel", before
`PouryaKit.Realtime` is built. Nothing of it was implemented when this note was recorded (2026-10-09; built 2026-10-18, the answer that follows). *Answered 2026-10-18: PostgreSQL `NOTIFY` on the delivery's own transaction, heard by one listener per instance, with "no second push
channel" read against SignalR beside SSE; ADR 0012 records it as built.*

Prerequisites for the Wave 3 spec, recorded 2026-10-10 from what Wave 2 built. Each restates I.3 rows against
the code and names no new Capability:

- *2026-10-10, Realtime row.* It references `Messaging` only, but `PouryaKit.Web` is the only place that begins the
  Operation Context at the HTTP boundary and trusts a peer Host. An SSE stream is an HTTP endpoint, so the spec says
  how it takes its caller's Actor and tenant through `Web`.
- *2026-10-10, gRPC row.* It omits `Web` too, and its interceptor that copies Actor, tenant, correlation and
  causation is a second header codec beside `Web`'s, which already writes those headers on an outgoing call and reads
  them only from a trusted peer (`PouryaKitTrustedPeerOptions`). The gRPC side uses `Web`'s, with no second codec.
- *2026-10-10, Multi-tenancy row.* Wave 2 built part of it: the tenant is on the Operation Context, in both header
  sets (an outgoing HTTP call's and the Outbox's), taken from the configured JWT claim, which `Security.JwtBearer`
  writes as `PouryaKitClaimTypes.Tenant`, and in the idempotency and cache keys. The row's Dapper lease that exposes the tenant as a parameter does not exist:
  the seam is `IReadConnectionFactory.OpenAsync`, a plain open connection. The Receipt key is (subscription, message
  id), so adding the tenant to it is a migration in every Module of every adopter that keeps Receipts.
- *2026-10-10, AI row.* I.4's resilience handler on the provider's HTTP client would stack on the one pipeline the
  service defaults give every client (`AddPouryaKitResilience()` in `ConfigureHttpClientDefaults`), and the Kit
  refuses a client with two resilience handlers by throwing each time it is created. So the provider's client keeps
  the Host's pipeline, or removes it with `RemoveAllResilienceHandlers()` before adding its own.
- *2026-10-10, Number sequences row.* The Kit maps a unique violation (`23505`) to a Module's Error on an EF save
  only: `IUniqueConstraintReader` reads a `DbUpdateException`. Plain SQL on the connection the Unit of Work joins
  (`IUnitOfWorkConnection`) is outside that mapping, so the spec says how the row's `Conflict` is reached.
- *2026-10-10, Saga row.* Its "Wolverine-discovered handler type" meets a Kit that turns Wolverine's conventional
  discovery off (`DisableConventionalDiscovery()` in `UsePouryaKitWolverine`) and routes each subscribed type to its
  own `Deliver` handler, which records the Receipt before the handler runs. The Saga's ADR and the Wave 3 spec name
  the seam a Saga is reached through and say whether Receipts and the Operation Context apply to its steps.
- *2026-10-10, the rows' proofs.* Several need test infrastructure the Kit lacks: two instances of a Host on one
  store (Realtime's client on another instance), S3 and SMTP containers (Storage, Email), an AppHost (Aspire
  resources) and an inspection of a Dapper query's SQL (Multi-tenancy's architecture test).
- *2026-10-10, providers chosen from configuration* (AI's among them). They need no new seam: `ModuleBuilder` is an
  `IServiceCollection` (`IModuleServices`), Inventory binds its options from its Composition Root with
  `BindConfiguration`, and Dapper resolves configuration inside its registered factory. The spec directs provider
  choice through options binding and a factory, keeping one declared composition.

A Wave 3 planning question that belongs to no I.3 row, recorded 2026-10-10: a Module that subscribes
to a Snapshot after data exists has no copy of an aggregate until it next changes (the Kit's skill, `messaging.md`, "What
the Kit guarantees for a Snapshot"). The Wave 3 spec decides whether an operator command writes the current Snapshot of
every aggregate through the Outbox for one subscription, under the existing version rules. *Answered 2026-10-24: decided no Snapshot backfill operator command in Wave 3, since the commit path cannot carry it and no Wave 3 item
needs it; it is a later spec's.*

A second Wave 3 planning question, recorded 2026-10-10: `TrackedState.Capture` copies every tracked
property of the Unit of Work's DbContext for each read-only step of an Operation (`EfUnitOfWork` captures with query loads
included), so a Wave 3 handler that nests many queries pays that capture each time. A measurement of that shape on 2026-10-10
found about 1 µs per tracked entity per nested query, half of a nested query's cost once
anything is tracked. Decide on that number for a representative Wave 3 Operation before its Capabilities rely on deep nesting.
*Answered 2026-10-24: the measured capture cost is accepted without change; a measurement row is added only if a
Wave 3 handler nests many queries over many tracked entities.*

Spikes before building (no owner input, recorded as ADRs): the Marten two-store spike (I.4); the
RabbitMQ topology spike under ADR 0006; a check whether Wolverine already propagates W3C trace
context into envelopes. *Resolved, ADR 0009 (2026-10-07): it writes and restores `traceparent` on the
local path; the Kit adds only the Operation activity's placement, `tracestate` and a parent fallback.*

### I.7 Recommended plan after Wave 1

Wave 1 is merged and the active [Phase 5 packaging and releases](#phase-5-packaging-and-releases) scope is complete.
All four Waves are built.
Use one spec per wave: **Wave 2** — paging, one JSON setup, Web
defaults, Web.OpenApi, Core, Security and JwtBearer, request idempotency, Caching, scheduling,
Observability, the HttpClient handler, the versioning rule, the RabbitMQ transport after its spike,
and the template's first two compositions; **Wave 3** — Marten after its spike, multi-tenancy, AI,
ReverseProxy (named Gateway until 2026-10-10), Grpc, Realtime through SSE, Storage, Email, number sequences, the optional reporting renderer
and opt-in report Module with its report job, optional localization, Aspire, the saga, a reusable
FluentValidation page rule in `PouryaKit.Validation` (an owner decision of 2026-10-07); **Wave 4** — the template's service composition and the sample agent polished for the
skill. Every ticket follows section H. Wave 1 stays as built.
