# Foundation kit: independent review and plan

Status: draft 6, 2026-10-04 (five Codex review rounds and the owner's decisions applied; see `foundation-review-log.md`). Analysis only — nothing was built, restored, run or modified in any
inspected repository. The kit is named **PouryaKit**. Every API shown under the `PouryaKit.*` prefix is
**proposed**, not existing CloudNativeKit/Woo functionality. *Phase 0 and Wave 1 have since been built from this plan
(closed 2026-10-06). Where the build departed from it, a dated note in place or an amendment in `docs/adr/` says so;
`docs/acceptance.md` maps the acceptance scenarios to tests, and the API as built is in the Kit's skill.*

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

Proposal docs (`playground/cloudnative-modular-example/`) were read only after the source
investigations finished. Package facts were checked against nuget.org and official docs on 2026-10-04.

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
├── PouryaKit.slnx  Directory.Build.props  Directory.Packages.props  THIRD-PARTY-NOTICES.md
├── src/
│   ├── PouryaKit.Domain/                 Result, Error, typed IDs, Entity, AggregateRoot, ValueObject,
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
  assigns its `Id` and `OccurredOn` once (ticket #6).
- The unique-constraint → Error mapping, `MapUniqueConstraint`, lives in `PouryaKit.Persistence.EFCore`;
  `PouryaKit.Persistence.Postgres` only reads the violated constraint's name from PostgreSQL's error (ticket #11).
- The typed-ID conventions (G.3) store a typed ID as its value and never make a key database-generated: a numeric
  typed ID the database generates needs `ValueGeneratedOnAdd()` in its entity's own mapping (ticket #8).
- An aggregate-specific repository (G.4) derives from `EfRepository` and gets one protected `ListAsync<TResult>` that
  returns a loaded list, never an `IQueryable` (ticket #12).
- `tests/` follows the three seams of spec #1's Testing Decisions, not one project per package:
  `ModularMonolith.Tests` (Gateway seam, the Proving Sample against PostgreSQL), `PouryaKit.Domain.Tests` (Domain seam)
  and `PouryaKit.ArchitectureTests` (Structure seam), plus `PouryaKit.Cqrs.Tests` (with its test Module
  `PouryaKit.Cqrs.Tests.Module`) and `PouryaKit.Validation.Tests`, which run the dispatcher and the validation step,
  `PouryaKit.Core.Tests`, which proves Core's three helpers (ticket #37), and `PouryaKit.Security.Tests`, which proves
  the permission registration's checks (ticket #38), all without a database.
- `PouryaKit.Persistence` (G.4), the thirteenth project, has no entry in the tree above and no row in the table
  below. It holds `IRepository` and `IUnitOfWorkConnection`, the seam through which a Module's plain-SQL write joins
  the Unit of Work (ticket #9), and references Domain and `Ardalis.Specification`. `PouryaKit.Persistence.EFCore`
  references it beside Cqrs, with `Ardalis.Specification.EntityFrameworkCore`.

### Allowed references

| Package | References | Third-party |
|---|---|---|
| Domain | — | none |
| Core (I.3, ticket #37) | — | none; referenced by Cqrs, Web, Messaging.Wolverine, Persistence.Dapper, Persistence.EFCore and Security since #55 (2026-10-07), whose registrations find their types through its `ImplementingTypes` |
| Cqrs | Domain, Core | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Logging.Abstractions` |
| Validation | Cqrs | FluentValidation |
| Modularity | Cqrs | `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.DependencyInjection` |
| Persistence.EFCore | Cqrs, Core | EF Core Relational |
| Persistence.Postgres | Persistence.EFCore | Npgsql EF provider, EFCore.NamingConventions |
| Persistence.Dapper | Cqrs, Core (#35, 2026-10-07: Cqrs for the Page types; Domain comes through Cqrs) | Dapper, Npgsql (ADO.NET driver only, no EF), `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` |
| Messaging | Domain (handlers return `Result`) | none |
| Messaging.Wolverine | Cqrs, Messaging, Core | WolverineFx |
| Messaging.Wolverine.EFCore | Messaging.Wolverine, Persistence.EFCore | WolverineFx.EntityFrameworkCore, WolverineFx.Postgresql |
| Web | Cqrs, Core | ASP.NET Core framework reference; `Microsoft.Extensions.Http.Resilience` (I.3, ticket #48: the standard outgoing retry, MIT; it brings Polly, BSD-3-Clause), no Aspire package |
| Security (I.3, ticket #38) | Cqrs, Core | none; no ASP.NET Core framework reference: the authenticated HTTP fallback policy is set by Web |
| Observability (I.3, ticket #47) | Cqrs (the plan listed Messaging too; nothing from it is used since the parent is recorded on the Operation) | none; `System.Diagnostics` only, no OpenTelemetry package, exporter or Serilog |
| Testing | Modularity, Persistence.Postgres, Messaging.Wolverine.EFCore | Testcontainers, Respawn, xUnit v3, `Microsoft.Extensions.TimeProvider.Testing` |

Amended on 2026-10-05, each addition MIT and already present in every Host:

- Modularity references `Microsoft.Extensions.DependencyInjection` (owner, ticket #13): the options that validate the
  container at build live only there, and `AddPouryaKitModules` turns them on in every environment.
- Persistence.Dapper references `Microsoft.Extensions.Configuration.Abstractions` (owner, ticket #9): the read
  connection is registered by connection name, as `UsePostgres` is. It also references
  `Microsoft.Extensions.DependencyInjection.Abstractions` explicitly (owner, ticket #9 review): its registration call
  on a Module's builder needs it, and the transitive copy Npgsql brings is not the pinned version. Its closure still
  holds no EF Core.
- Testing references `Microsoft.Extensions.TimeProvider.Testing` (ticket #8): the test clock of ADR 0005, a decision
  the owner delegated.
- Persistence.EFCore also references Persistence and `Ardalis.Specification.EntityFrameworkCore`, as G.4 decided.

Standing rule (owner, 2026-10-06), extending the amendments above: an MIT-licensed `Microsoft.Extensions.*`
package may be referenced where a package needs it, and is added to this table in the same change. Any other
reference outside the table stays an owner decision.

Checks against your rules: Dapper does not reference EF. Postgres does not reference Wolverine. Web
does not reference Modularity. Nothing chooses an identity provider. `Core` exists since Wave 2 (#37) and is capped at three public types by an architecture test; there is no `Utils` (corrected 2026-10-07).
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
  (`CustomClaims.cs:26-66`, `FeatureAuthorizationHandler.cs:11-18`). *Corrected 2026-10-07 (#38): Wave 2 adds
  `PouryaKit.Security` → `Cqrs` with permission contracts and an optional `IAuthorizer` implementation only; the
  authenticated `FallbackPolicy` stays in `PouryaKit.Web`, which owns the HTTP boundary, and the application still
  configures JwtBearer (until #39).*

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
| Multi-tenancy | `PouryaKit.MultiTenancy` | 3 | Ultrabusiness Owner/CompanyBranch (idea only) | tenant in `OperationContext`, EF query filter, receipt key, message header |
| Observability | template content + small `PouryaKit.Observability` | 2 | Aspire ServiceDefaults; Cybernod `ObservabilityPipelineBehavior` | command/query activity + metrics step, trace context through outbox headers |
| Health, resilience, service discovery | template content (Aspire ServiceDefaults) | 2 | Aspire 13.6 template | none — native; liveness must not include external dependencies |
| Logging provider (Serilog) | template option | 2 | Cybernod bootstrap | none |
| Aspire AppHost resources | `PouryaKit.Aspire` (AppHost-only) | 3 | CNK `AspireIntegrations` | only resources actually needed |
| RabbitMQ transport, microservice profile | `PouryaKit.Messaging.Wolverine.RabbitMq` | 2 (contracts are transport-neutral from wave 1) | CNK/Woo topology helpers | per-service queue naming, durable outbox on routes |
| Realtime (SignalR/SSE), file storage, email | optional packages | 3 | Cybernod SignalR, Ultrabusiness SSE/S3, CNK Email | thin, provider-per-package |
| Number sequences, reporting, AI | optional packages, only if reused across projects | 4 | Ultrabusiness, Cybernod | decide per capability; AI and reporting stay app-level by default |
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
        module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
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

Needs Phase 4. Wave 1 is merged. On 2026-10-06 the owner narrowed the active scope to "packaging versioning and nuget packages in cleanest way and efficient if any sample references covers all follow them". The existing shared package validation and standard private GitHub Packages publisher are delivered. This replaces the custom publication protocol and preflight machinery from this wave. #28/#29 compatibility automation, named cuts and content-verified recovery are closed as not planned and require a new scoped ticket, and the current scope does not claim all original #24 acceptance criteria. Section G and the ADRs remain unchanged.

Keep central package management, MinVer as the only version engine, SourceLink, `EnablePackageValidation`, MIT and `THIRD-PARTY-NOTICES.md` in each nupkg. All thirteen shipped Kit packages share one version. Private GitHub Packages comes first. Nuget.org activation and stable `1.0` each require a later owner decision.

MinVer uses minimum major/minor `0.1`, tag prefix `v` and default prerelease identifiers `preview.0`. Fetch full history and tags. Untagged main commits use Git height, such as `0.1.0-preview.0.H`. Owner-created `v0.x` preview/rc tags on current main supply exact named versions when the owner then runs CI manually on main. No workflow creates a tag or GitHub Release, and no version override or second calculation is added. See [MinVer's pinned README](https://github.com/adamralph/minver/blob/9f485aabb9936efd8fb8b633ae09ae4d0659c94f/README.md#L179) and [private package guidance](private-packages.md#versions-and-triggers).

| Trigger | Active behaviour |
|---|---|
| Pull request, tag push or manual CI on a non-main branch | Read-only build, tests, pack, dependency checks and package-only consumers. No publishing, tag/release writes or release credentials. |
| Successful main push CI | Publish original validated MinVer `0.x` preview/rc packages to the fixed private feed. |
| Manual CI on main after an owner-created prerelease tag | Validate that main SHA with full tags, then publish its original packages at MinVer's tag version. |
| Manual private publication on main | Select the original successful main CI run, attempt and SHA. Download the same retained packages and manifest. |

The read-only authorization job requires successful CI and AI/package-validation jobs, the fixed private repository, exact main source SHA and attempt, and a producer event of `push` or `workflow_dispatch`. PR/tag-head artifacts cannot authorize publication. The publisher uses trusted main code, official artifact download, the original receipt/inventory/hash checks, standard `dotnet nuget push` and a clean all-thirteen private-feed restore. It never builds replacement packages. The existing literal concurrency group disables cancellation of an active publisher; GitHub's one pending slot is not a durable queue.

`--skip-duplicate` skips HTTP 409. It does not verify matching remote contents or fulfill the original identical-content recovery guarantee. Successful [retry run 37465070135](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465070135) completed `0.1.0-preview.0.10` from original main CI `37456433718`, attempt `1`, artifact `11408539884`, source `2de265641b8cd94a5ddc17980cdcad530f0a8f60`, skipping Domain on HTTP 409, pushing twelve packages and restoring all thirteen with fresh caches. Preserve the original artifacts and retry record; never delete, replace or repack that version. This adds no remote raw-byte identity proof.

**Verified reference sources.** These are static source checks, not successful upstream publication evidence:

- [`meysamhadeli/woo@149a3cf168496c3bec85945f83ad222e43720e4d .github/workflows/publish.yml`](https://github.com/meysamhadeli/woo/blob/149a3cf168496c3bec85945f83ad222e43720e4d/.github/workflows/publish.yml) supplies the common Release build at line 52, `pack --no-build` at lines 59–65 and standard NuGet push at lines 89–93. Keep the Kit's existing exact inventory and MinVer instead of its count-only gate, separate version input and public-feed secret. Duplicate skipping is accepted with the limited HTTP 409 meaning above.
- [`mehdihadeli/CloudNativeKit@e81d65e6376008cb001fdbd886086dc6e84790c6 .github/workflows/build-and-publish.yml`](https://github.com/mehdihadeli/CloudNativeKit/blob/e81d65e6376008cb001fdbd886086dc6e84790c6/.github/workflows/build-and-publish.yml) supplies full history and build/test gating at lines 26, 36–43 and 76–78. Do not adopt its separate version calculation, repeated publication build or release drafting under `always()`.

**Active acceptance:**

1. Package metadata and consumer proof remain the existing #25 path. Evaluate the nonempty thirteen-package inventory, build Release once, test with `--no-build`, pack with `--no-build`, check metadata/SourceLink and package graph rules, and restore/build every package and supported profile in clean package-only consumers.
2. CI remains read-only and preserves the tested SHA, MinVer version, package inventory, hashes and original artifacts. Successful main producer metadata authorizes a separate package-write job; failure or PR provenance cannot authorize it.
3. Publish originals to the fixed private feed with the ephemeral job token, preserve the manifest, and restore all thirteen exact versions with fresh caches. Keep public publication and tag/Release writes disabled. Record actual hosted evidence in [#27](https://github.com/PouryaNoufallah96/PouryaKit/issues/27), with the existing fork-execution exception in #26.

**Original acceptance closed as not planned:** #28/#29 described packaged public API comparison, reviewed compatibility/progression and migration notes, approved immutable named cuts, tagged validation, tag/package/manifest version agreement, GitHub Release announcement only after complete publication, remote raw-byte/SHA conflict checks and partial-publish identical-content recovery from original artifacts. These are historical requirements outside the completed active #24 scope, not guarantees of standard duplicate skipping. [Release scope guidance](releases-preflight.md) records this boundary. Resuming that work needs a new scoped brief; MinVer remains the sole version engine.

The active scope's value is validated private packages with one version and a clean consumer restore. Its cost is the existing package validator plus standard Actions/CLI jobs and small provenance checks. Its impact is packaging and CI only; Kit runtime APIs, reference projects, credentials and access settings are unchanged. The active Phase 5 scope is complete: [CI run 37465576160](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465576160) and [publication run 37466084084](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37466084084) succeeded at main SHA `b234f4f`, with 386 runtime tests passed, zero failures or skips, thirteen validated packages, fifteen clean consumers and the private-feed restore at MinVer `0.1.0-preview.0.13`. The broader original #24 acceptance was not delivered; #28/#29 are closed as not planned.

**Phase 6 — template and second profile** (optional, after 5)
- `dotnet new` template extracted from the proven sample; single-service profile with RabbitMQ.

**Out of scope: existing projects.** The kit is for new projects. Cybernod, Ultrabusiness and the
other inspected codebases are sources of ideas only; nothing in this plan changes, migrates or
refactors them. (An earlier draft had a "Cybernod adoption" phase; the owner removed it on 2026-10-04.)

### Provenance and licensing

- CNK, Woo, food-delivery, vertical-slice (Hadeli) and mm-ddd (Grzybek) are MIT with no NOTICE files.
  Keep each copyright line and the MIT text in `THIRD-PARTY-NOTICES.md`; record source commit and
  path per extracted file.
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
Remaining unverified work requires its own proof. The private-fork verification exception remains,
and #28/#29 stay closed as not planned.

**Decided by the owner (2026-10-04):**
1. **One host container.** Modules keep their own composition root, gateway, operation scope,
   DbContext and schema; no private `ServiceProvider` per module.
2. **Adoption rule.** Take the best implementation from any shared source and customise it, but the
   result must be one consistent design — one implementation per concern, one naming scheme. The
   kit's own docs and provenance records cite upstream repository, commit and repo-relative path,
   never a local machine path. Ultrabusiness ownership is unconfirmed, so it is adopted at pattern
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
7. The "no local files" rule is confirmed as a docs/provenance rule.
8. The owner authorised finalising this plan and splitting it into phases worked through the
   repo's skills. Building has not started. *Phase 0 and Wave 1 were built by 2026-10-06 (spec #1).*

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

### Found incidentally in Cybernod (not part of this plan)

- Single-scan delete has no ownership check in three modules (`AggregateRootByIdSpec.cs:14`).
- `GET identity-module/subscription/scan-availability/by-user` is anonymous.
- Unknown exceptions return their message to clients (`UnknownExceptionProblemDetails.cs:26`).
- `isDev && isTest` in `Program.cs:80,124,158` is never true.
- A failed `Result` commits today (`SubmitInternalScanResultsCommandHandler.cs:40,52-55`).

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

Written after Wave 1 closed (`feat/wave-1` at `dfbe099`, 313 tests green). It is the catalogue of
everything a Host may register after Wave 1. It adds to sections C and G and does not change them;
where an older row of the "Full target catalogue" differs, this section wins. Section G and the
decision records in `docs/adr/` are untouched; what this section contradicts in them is listed in I.6
as follow-up decisions and ADR amendments to record when the capability is built. Sources are cited
as `repository@commit path:line`; Ultrabusiness is cited as `Ultrabusiness@a85eacd` (private,
pattern only, no file copied). Package facts were read from nuget.org and the libraries' own
repositories on 2026-10-06. Nothing here is implemented.

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
service, `IDispatcher` in the monolith — today's sample endpoints are the former, `samples/ModularMonolith/.../CreateProductEndpoint.cs:18`).
The monolith composition gets its own HTTP smoke test without `Modularity` registered. Inside one Host,
a call between Modules goes through the Gateway, never through YARP or gRPC loopback.

### I.2 Decisions applied

1. **Paging** (first follow-on after Wave 1; not part of ticket #9, which shipped an unpaged list).
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

2. **Event sourcing** is in the catalogue as `PouryaKit.Persistence.Marten` (Marten 9.46.0 on
   PostgreSQL, MIT). An aggregate that needs it is a **Stream**: load by folding its events, append
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
Wave 2 is the first follow-on Capability set after the merged Wave 1; Wave 3 needs Wave 2 pieces;
Wave 4 is the sample agent and the template's third composition.

| Capability | Form | Package or home | Wave | Adopted from (`repo@commit path:line`) | What the Kit adds | Proof |
|---|---|---|---|---|---|---|
| Paging | R | `Cqrs`, `Persistence.Dapper`, `Persistence.EFCore`, `Web` | 2 | see I.2.1 | page types, allow-listed sort, one HTTP page shape (*built 2026-10-07, #35, with allow-listed filters added at the owner's request in spec #33: a `PageDeclaration` beside the query names its sort keys and filter fields only, and each helper maps the names, Dapper to a column per key with each filter as the parameter `@{field}_{operator}` of the Feature's own `WHERE`, EF Core to an expression per key and a predicate per filter, instead of I.4's `SortBy<ProductDto>(x => x.Name, "name")`; the query's validator calls `PageDeclaration.Validate`; `Persistence.Dapper` references `Cqrs` in place of `Domain` for the Page types, as section C's table records*) | paged `ListProducts` through Gateway and HTTP; unknown sort key → validation Error; page beyond end → empty page with total |
| One JSON setup | R | `Domain` (options), used by `Web` and `Messaging.Wolverine` | 2 | Wave 1 has two separate registrations (`src/PouryaKit.Web/WebServiceCollectionExtensions.cs:37`, `src/PouryaKit.Messaging.Wolverine/WolverineHostApplicationBuilderExtensions.cs:47`) | one `JsonSerializerOptions` factory for typed IDs, HTTP and messages; gRPC stays protobuf | a typed ID round-trips identically through HTTP and an envelope |
| CORS, response compression, rate limiting | R | `Web`, from configuration | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Web/Extensions/CompressionExtensions.cs:21-34`; `RateLimit/RateLimiterExtensions.cs:33-47` (429 through `IProblemDetailsService`, fits ADR 0008); `CloudNativeKit@e81d65e src/CloudNativeKit.Web/Cors/DependencyInjectionExtensions.cs:34-40` | all three live in the shared framework (no package); named CORS policy from configured origins, **no** `AllowAnyOrigin` fallback (rejected: `CorsExtensions.cs:47`); partition by Actor, never by `X-Forwarded-For` or Host header (rejected: `RateLimiterExtensions.cs:71-72,109-111`); `QueueLimit` configured separately | 429 is a problem response; disallowed origin refused; compression on for HTTPS when enabled |
| API versioning, OpenAPI, Scalar | P | `PouryaKit.Web.OpenApi` → `Web`; `Asp.Versioning.Http` 10.2.3, `Asp.Versioning.OpenApi` 10.2.3, `Microsoft.AspNetCore.OpenApi` 10.0.12, `Scalar.AspNetCore` 2.17.13 (all MIT) | 2 | `woo@149a3cf src/Web/ApiVersioningExtensions.cs:19-49` (header + URL readers, `'v'VVV`, substitute in URL); `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Web/OpenApi/OpenApiVersioningDocumentTransformer.cs:27-38,64-111`; `CloudNativeKit@e81d65e src/CloudNativeKit.Web/Minimal/Extensions/MinimalApiExtensions.cs:59-70` | per-Module route group gets a version set; documents enumerated from the version set, never a hard-coded `["v1"]`; sunset dates from configuration, not "now + 60 days" (rejected: `VersioningExtensions.cs:33-34`, ADR 0005); security requirements per operation (rejected: Woo's transformer adds schemes only) | one document per version; Scalar serves them; deprecated version reports sunset |
| Gateway | P | `PouryaKit.Gateway`, its own Host; `Yarp.ReverseProxy` 2.3.0 (MIT; latest release targets net8, runs on .NET 10 through that asset — re-check before Wave 3), `Microsoft.Extensions.ServiceDiscovery.Yarp` 10.10.0 | 3 | `cybernod@bae8ac3 src/API/Cybernod.ApiGateway.Yarp/Program.cs:40-54`; `Ultrabusiness@a85eacd src/ApiGateways/ApiGateway.Yarp/Configurations/Extensions/HostExtensions.cs:34-60` | config-loaded routes and clusters with service discovery; W3C trace context passed through (rejected: both mint a random `TraceId` header); health probe is `/health`, not the docs UI | a route to a service resolves by discovery; `traceparent` arrives unchanged |
| gRPC | P | `PouryaKit.Grpc`; `Grpc.AspNetCore` 2.84.0 (Apache-2.0), `Grpc.Net.ClientFactory` 2.84.0, `Google.Protobuf` 3.36.2 (BSD-3) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Presentation/MainArchitecture.API/Configurations/Extensions/GrpcExtensions.cs:68-89`; `MainArchitecture.SharedGrpcContracts/custom_types.proto` (decimal, guid) | client factory with one interceptor that copies Actor, tenant, correlation and causation (never a bearer token: rejected `Interceptors/GrpcExceptionInterceptor.cs:48-81`); deadline from configuration; server side maps `Result` to gRPC status; protobuf contracts only; **not a loopback inside one Host** (architecture test) | a cross-process call carries the Operation Context; a failed `Result` is a gRPC status with the Error |
| Core helpers | P | `PouryaKit.Core`: predicate builder, reflection helpers, synchronization-context scope — nothing else (amended 2026-10-07, #55: `ImplementingTypes` gains `Candidates`, the candidate set a registration validates; the three-public-type cap stands) | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Core/Utils/NoSynchronizationContextScope.cs:12-27`; predicate-builder visitor `Linq/PredicateBuilder.cs:19-41` | `AndAlso`/`OrElse` (both references use bitwise `And`/`Or`: `cybernod … PredicateBuilder.cs:25,40`; `CloudNativeKit … Linq/PredicateBuilder.cs:22,32`); reflection limited to implementing-type lookup (rejected: 1,782 lines returning `dynamic`); architecture test caps the package at these three | an architecture test fails if a fourth public type appears |
| Security | P | `PouryaKit.Security` → `Cqrs` | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Security/Jwt/Extensions/AuthExtensions.cs:53-59` (authenticated default policy); `FeatureAuthorizationHandler.cs:8-31` minus the admin bypass | permission contracts; an `IAuthorizer<T>` implementation that evaluates a declared permission against the Actor — optional, registered by the application for the commands it chooses (G.6) (*since #56, 2026-10-07: a declared permission must be registered by its Module's Composition Root, or the Host fails at build*); ASP.NET `FallbackPolicy` = authenticated at the HTTP boundary (*built 2026-10-07 in `PouryaKit.Web`, which owns the HTTP boundary; #38*); no runtime mutation of `AuthorizationOptions` (rejected: `FeaturePolicyProvider.cs:20-24`); optional PBKDF2 password hashing, an owner scope addition of spec #33 (*built 2026-10-07: PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt, 32-byte key, versioned `pbkdf2$1$…` encoding with upgrade detection, platform cryptography only, chosen from the OWASP Password Storage Cheat Sheet and the .NET 10 `Rfc2898DeriveBytes.Pbkdf2` reference; #40*) | unauthenticated HTTP → 401; a command with a declared permission and the wrong Actor → `Forbidden` Error → 403; a continuation runs without re-authorization (*since #57, 2026-10-07: a continuation is trusted only when the Kit marks it `IsTrustedContinuation`, restoring a delivery or continuing a running authorized Operation; a context application code builds, or continues outside the Operation that ran it, is checked; `CausationId` is informational*) |
| JWT bearer | P | `PouryaKit.Security.JwtBearer` → `Security`; `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12 (MIT) | 2 | `cybernod@bae8ac3 … Jwt/Extensions/ClerkExtensions.cs:95-149` minus Clerk | claim normalisation into the Actor and tenant from configured claim names; audience validation never silently off (rejected: `ConfigureJwtBearerOptions.cs:44`); no provider SDK, no token issuance, no API keys, no PKCE, no baked role policies (rejected: `CloudNativeKit … Security/ApiKey/InMemoryGetApiKeyQuery.cs:15-30`; `woo … src/Jwt/Woo.Jwt.csproj` Duende) | a token from a test issuer becomes the expected Actor and tenant |
| Request idempotency | R | `Cqrs` (contract) + `Persistence.EFCore` (key table) + `Web` (header) | 2 | design only: `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.Idempotency/Middlewares/RequestIdMiddleware.cs:45-83`, key `(Id, Name)` | the key is **reserved inside the command transaction** as a row keyed by `(tenant, Actor, command name, request id)` — tenant is part of the key from the first version, a null tenant stored as a fixed sentinel so single-tenant Hosts work unchanged; a replay returns the stored outcome; a failed command stores nothing, so its rollback releases the key; a concurrent duplicate waits on the row (rejected in both references: marker written after the handler on its own connection, replay returns a default success — `Types/Commands/Behaviors/IdempotencyBehavior.cs:58-93`, `IdempotencyManager.cs:30-40`; `cybernod … Inbox/Idempotency/IdempotencyService.cs:39-66`) | replay of a success returns the same response and writes nothing; replay after failure re-executes; two concurrent sends produce one effect; the same Actor, command and key in two tenants are two Operations |
| Caching | P | `PouryaKit.Caching` → `Cqrs`; `Microsoft.Extensions.Caching.Hybrid` 10.10.0 (MIT); Redis through `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.12 as the application's `IDistributedCache` | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Caching/Services/HybridCachingService.cs:12-69`; `Messaging/Queries/ICacheableQuery.cs:6-37` | `ICacheableQuery` with key, duration and tags; one `HybridCache.GetOrCreateAsync` (single-flight per instance), **never a failed `Result`** (rejected: `Middlewares/CachingBehavior.cs:50-84` get-then-set caches failures); a key always names its Actor and tenant, while tags name the resource (`product:{id}`) so one invalidation reaches every Actor's entry; **a query dispatched inside an active write Unit of Work bypasses the cache** both ways, since it may see uncommitted state (`src/PouryaKit.Cqrs/Dispatcher.cs:204-207`, `src/PouryaKit.Persistence.EFCore/EfRepository.cs:43-46`); a command declares the tags it invalidates and the eviction runs after the outermost successful commit, never on rollback; every writer of a cached resource declares its tag (the sample: `RenameProduct` **and** `RemoveProducts` invalidate `product:{id}`) | a failed query is not cached; two Actors never share a key; a nested query during a command that then rolls back leaves no entry; a warm `GetProduct` after `RemoveProducts` is `NotFound` |
| Scheduled and delayed work | R | `Messaging.Wolverine` (+ `.EFCore` for durability) | 2 | Wolverine scheduled messages (`JasperFx/wolverine@1f22835 docs/guide/messaging/message-bus.md:412-428`: durable only on a durable outbox endpoint) | two distinct forms, as G.6 and ADR 0007 require: **delayed continuation** — `Schedule(command, at)` from inside an Operation is written through its Outbox and runs with that Operation's Actor, tenant, correlation and causation (`OperationContext.Continue()`; *since #57, 2026-10-07: it stays a trusted continuation only when its delivery restores the context through Messaging.Wolverine's `ContinuedOperationContext`*); **independent recurring work** — declared in a Module's Composition Root with a named system Actor, durable through the message store; neither takes system authority by default; no Quartz (rejected: Cybernod's in-memory Quartz store loses jobs on restart). *Built 2026-10-07 (#44): the delayed continuation is `IDelayedCommands.Schedule(command, at)` for a command its Module declared with `AddPouryaKitDelayedCommand`, written in the Outbox step and delivered on its own durable queue with a Receipt; the store fires it on the system clock, not the Operation's `TimeProvider` (ADR 0005 and ADR 0006, amendments for #44). Recurring work is #45.* | a delayed command survives a restart and runs once as the scheduling Actor; a recurring declaration runs as its named system Actor; neither runs as the other |
| Observability | P | `PouryaKit.Observability` → `Cqrs` (the plan listed Messaging too; nothing from it is used since the parent is recorded on the Operation); `System.Diagnostics` only (exporters are template content) | 2 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Core/Observability/Behaviors/ObservabilityPipelineBehavior.cs:37-74`, `CommandHandlerMetrics.cs:13-37,119` (correct `TotalSeconds`; rejected: Woo/CNK record whole seconds) | one `ActivitySource` per Operation with command/query tags and duration metrics; `traceparent`/`tracestate` written into Outbox headers and restored on delivery (if Wolverine already propagates W3C context, the Kit relies on it — the spike in I.6 checks) (*built 2026-10-07, #47, as ADR 0009 decided: `AddPouryaKitObservability` wraps the scoped dispatcher, so one `pouryakit.operation` activity per command or query, of the source `PouryaKit`, is current around the whole Operation and its Outbox write, tagged with Module, operation, Actor kind and outcome, with a `pouryakit.operation.duration` histogram in fractional seconds; Wolverine carries `traceparent` as the envelope's `parent-id`, the Outbox adds the `pouryakit-tracestate` header, and every delivery records both on the `Operation` (`Operation.TraceParent`), so its activity's parent is Wolverine's span when one is sampled and the publishing Operation otherwise; local proof only, RabbitMQ is #52*) | a delivery's activity is a child of the sending Operation's trace |
| Health, resilience, service discovery, Serilog | T | Aspire ServiceDefaults template | 2 | Aspire 13.6 template | none; liveness checks no external dependency | template compiles in all three compositions |
| HttpClient context handler | R | `Web` (or `Security` if it needs the Actor type only; *built 2026-10-07 in `PouryaKit.Web`, which owns both ends of the HTTP boundary, with the standard resilience handler; #48*) | 2 | shape: `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Security/Utilities/HttpClientAuthorizationDelegatingHandler.cs:14-33` | a `DelegatingHandler` that copies Actor, tenant, correlation and causation headers onto an outgoing call; **never the caller's bearer token** (G.6; rejected `:23-30`); the receiving Host's `Web` reads the same headers only from a configured trusted peer | headers present on the outgoing request; a token is never forwarded |
| Integration Event versioning | R | `Messaging` rule, enforced at startup | 2 | Wave 1 `[WireName]` convention | one wire name carries one version (`catalog.product-created.v1`); a new version is a new wire name and a new contract type; both may be published for a transition; startup fails on two types with one wire name | startup check; a subscriber of v1 does not receive v2 |
| RabbitMQ transport | P | `PouryaKit.Messaging.Wolverine.RabbitMq` → `Messaging.Wolverine`; `WolverineFx.RabbitMQ` 6.46.0 (MIT) | 2 | `woo@149a3cf src/Wolverine/Extensions.cs:64-88` (connection, auto-provision); `CloudNativeKit@e81d65e src/CloudNativeKit.Integration.Wolverine/WolverineMessageTopologyExtensions.cs:35-75` (exchange per message, queue per listener, `UseDurableInbox`) minus its static registries | per-subscription queue naming so two services never compete for one queue; durable inbox on every listener (conventional routing creates listeners without it: `docs/guide/messaging/transports/rabbitmq/conventional-routing.md:28-34`); same Receipts and headers as local delivery | **spike first** (I.6): one RabbitMQ queue per subscription versus receive-once-then-local-queues under ADR 0006 |
| Multi-tenancy | R (note, not a package) | `Cqrs` (tenant already on `OperationContext`), `Persistence.EFCore` (global filter), `Messaging` (header exists), `Security.JwtBearer` (tenant claim) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Infrastructure/Identity/InternalRequestContext.cs:13-34`; `BuildingBlocks.Persistence/Extensions/EntityFrameworkCoreModelBuilderExtensions.cs:33-55` (composed filters) | tenant set only from a verified claim, never from a client header (rejected: `Middlewares/OrganizationInformationMiddleware.cs`); EF query filter from `OperationContext.Tenant` **and** a tenant check in the prepare step (a tracked aggregate of another tenant fails the save); the Dapper lease exposes the tenant as a parameter and every read and count in a tenant-scoped Module carries `tenant_id = @Tenant` — an architecture test fails a Dapper query in such a Module without it; projection tables are keyed by `(tenant_id, product_id)` and Integration Event and Snapshot handlers apply under the delivered tenant; Receipt key gains the tenant; a Stream uses Marten conjoined tenancy with the same tenant id; an agent tool inherits the tenant through the Operation Context it runs under | a tenant cannot read (Gateway, HTTP, Dapper), write or receive (Snapshot delivery) another tenant's rows or Receipts; a cross-tenant aggregate load fails at save |
| Event-sourced write model | P | `PouryaKit.Persistence.Marten` → `Cqrs`, `Messaging`; Marten 9.46.0; plus `WolverineFx.Marten` 6.46.0 in `Messaging.Wolverine.Marten` | 3 | names only from `woo@149a3cf src/EventStoreDB/Repository/EventStoreDBRepository.cs:7-16` (`Find`/`Add`/`Update(expectedVersion)`); `ExpectedStreamVersion` `NoStream`/`Any` from `CloudNativeKit@e81d65e src/CloudNativeKit.Abstractions/Persistence/EventStore/ExpectedStreamVersion.cs:3-7` | see I.4 | see I.4 spike and acceptance |
| AI | P | `PouryaKit.AI` → `Cqrs`, `Modularity`; vector store in `PouryaKit.AI.Postgres` | 3 (after scheduling) | `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.AI` (pattern) | see I.4 | see I.4 |
| Realtime | P | `PouryaKit.Realtime` → `Messaging`; `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 10.0.12 | 3 | `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.SignalR/Extensions/SignalRExtensions.cs:24-41` | SignalR with a Redis backplane and a channel prefix; detailed errors off outside Development (rejected `:29`); a Notification Handler pushes; no per-node connection sets (rejected `SignalRConnectionManager.cs:23`; Ultrabusiness SSE is per instance) | a Notification reaches a connected client after commit |
| File storage | P | `PouryaKit.Storage` (contract) + `.S3` / `.AzureBlob` providers | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/BuildingBlocks.FileManagement/Contracts/IFileStorageProvider.cs:18-59` (contract shape); `cybernod@bae8ac3 … FileUpload/Extensions/AzureStorageExtensions.cs:22-40` | one contract, one provider per package (`AWSSDK.S3` Apache-2.0; `Azure.Storage.Blobs` 12.30.0 MIT) | upload/download/presigned URL per provider in a container test |
| Email | P | `PouryaKit.Email` + `.MailKit` (`MailKit` 4.18.1 MIT) | 3 | `CloudNativeKit@e81d65e src/CloudNativeKit.Email/IEmailSender.cs`, `MailKitEmailSender.cs:29-34` | `CancellationToken` on every call; a client per registration, not per send (rejected: `SendGridEmailSender.cs:22`); sent from a Notification Handler | a Notification Handler sends through a test SMTP container |
| Saga | P (optional) | `PouryaKit.Messaging.Wolverine.Sagas` | 3 | Wolverine sagas (`docs/guide/durability/sagas.md:11-20,556-604,617-630`) | native Wolverine `Saga` with PostgreSQL storage and `TimeoutMessage`; approved 2026-10-06 as the one documented exception to ADR 0001/0006 (a public, Wolverine-discovered handler type) and a fourth, optional kind of reaction beside G.2 | a saga started by one Integration Event, advanced by a second, and timed out by `TimeoutMessage`, each step through the outbox |
| Number sequences | R | `Persistence.Postgres` (optional, only a reusable sequence) | 3 | `Ultrabusiness@a85eacd src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Persistence/Numbering/NumberSequenceAllocator.cs:11-47` (runs on the current transaction) | PostgreSQL `SELECT … FOR UPDATE` on a sequence row inside the Unit of Work; gap-free per scope key; `23505` mapped to `Conflict` | two concurrent Operations get consecutive numbers; a rolled-back Operation leaves no gap |
| Reporting job seam | R | `Messaging.Wolverine` scheduling + a `claim-and-run` aggregate pattern in the sample | 3 | `Ultrabusiness@a85eacd … MainArchitecture.Domain/Aggregates/ReportJobAggregate/Entities/ReportJob.cs:294-313,332-421,478-481` (`TryClaimForGeneration` fenced by attempt id) | the claim-and-settle seam as a documented pattern on an EF aggregate with the Kit's `Version`; report bodies stay in the application; no Quartz | a stale claim is taken over once; a late settle of a superseded attempt is refused |
| Aspire resources | P | `PouryaKit.Aspire`, AppHost-only; `Aspire.Hosting.PostgreSQL`/`.Redis`/`.RabbitMQ` 13.6.0, `CommunityToolkit.Aspire.Hosting.Ollama` 13.6.0 (all MIT) | 3 | `CloudNativeKit@e81d65e src/CloudNativeKit.AspireIntegrations/Postgres/PostgresBuilderExtensions.cs:93-189` (pinned images, volumes) | PostgreSQL, Redis when caching is registered, RabbitMQ when that transport is registered, Ollama when an agent runs locally; pinned images; no Jaeger, Zipkin, Tempo, Loki, Kibana, Prometheus, Grafana, Elasticsearch or Swagger resource | AppHost starts the Proving Sample with each resource set |
| Project template | T | `dotnet new` template generated from the Proving Sample | 2 (monolith + modular monolith), 4 (service composition) | — | three compositions: the same business code, generated Composition Root and endpoint adapter per composition | each composition builds; the monolith passes an HTTP smoke test without `Modularity` |
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
so ADR 0001 isolation holds. Notifications and Snapshots for a Stream are built from the events
appended in that commit and the stream version after the append, since the EF path's
`changedAggregates` seam does not see them (`src/PouryaKit.Messaging.Wolverine.EFCore/WolverineOutbox.cs:57-58`).
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
`IReceipts` is selected per receiving Module by its write model instead of one global registration.
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
model as a failed tool result, never thrown. Iteration cap and per-run tool allow-list are
configuration. Packages (verified 2026-10-06, all MIT unless stated): `Microsoft.Extensions.AI`
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
choose tools). Scheduled agent work uses Wolverine scheduling under a system Actor.

**Saga** (approved): a Wolverine `Saga` type with PostgreSQL storage, timeouts through
`TimeoutMessage`, started and advanced by Integration Events through the Kit's routing; the saga's
own messages go out through the same outbox.

**Observability.** The Kit emits; the template exports. Activities: `pouryakit.operation` with
module, operation, actor kind, outcome; delivery activities link to the publishing Operation. *Resolved, ADR 0009
(2026-10-07): a child relationship, not a link; see the record for what #47 implements.*

### I.5 What the Proving Sample gains after Wave 1 (nothing enters Wave 1)

| Wave | Addition | Shows |
|---|---|---|
| 2 | `ListProducts` paged; `Idempotency-Key` on `CreateProduct`; `GetProduct` cached with tag invalidation on `RenameProduct` and `RemoveProducts`; a delayed `ExpireQuote` continuation (runs as the quoting Actor) and a recurring `ReconcileStock` declaration (runs as a named system Actor); versioned OpenAPI with Scalar; CORS/compression/rate limiting from configuration; a JWT test issuer and one permission-declared command; a trace that spans Catalog → Inventory delivery | paging, idempotency, caching, scheduling, OpenAPI, web defaults, security, observability |
| 2 | Inventory run as its own process over RabbitMQ (the service composition), still passing the Wave 1 delivery tests | RabbitMQ transport, the template's third composition |
| 3 | a Stream Module `Ledger` beside Catalog: one command appends, one projection reads, one Integration Event leaves through the existing messaging | event sourcing |
| 3 | tenant-scoped Catalog rows, Receipts and Snapshots | multi-tenancy |
| 3 | one agent with one tool, `GetProduct` through the Catalog Gateway, run as the calling Actor; Ollama in the AppHost | AI |
| 3 | an email and a realtime push from a Notification Handler; a file upload behind the storage contract | the thin packages |
| 3 | AppHost with PostgreSQL, Redis, RabbitMQ and Ollama | Aspire |

### I.6 Follow-up decisions and ADR amendments

Decided above and needing only an ADR amendment when built (no owner input):

- **ADR 0003 and 0004** gain: "each write model has one commit owner; the EF Unit of Work owns rows
  and commits through the EF outbox; the Marten Unit of Work owns a Stream and commits through
  Wolverine's Marten outbox; no Operation commits through both." Recorded in the Marten spike's ADR.
- **ADR 0002** gains: the fixed pipeline has two optional slots, idempotency (before the Unit of
  Work) and caching (around a query), filled by `PouryaKit.Caching` and the idempotency registration
  only; applications still register no steps.
- **ADR 0006** gains the RabbitMQ answer once its spike runs.
- Catalogue rows superseded by this section: AI (was "application by default, Wave 4"); caching
  (section B said "native, no kit package"; this section gives it a package for the Result and key
  rules); the Security row's "also covers background commands" (contradicts G.6: continuations are
  trusted and not re-authorized; the hook is optional).

Answered by the owner on 2026-10-06:

1. **Saga:** yes — `PouryaKit.Messaging.Wolverine.Sagas`, optional, Wave 3, with the documented
   exception to ADR 0001/0006 and G.2 recorded in its ADR when built.
2. **Stream commit owner:** yes — Marten stays optional over PostgreSQL, with Kit-controlled commits
   through Wolverine's Marten outbox, subject to the spike in I.4 passing.
3. **AI providers:** `Microsoft.Extensions.AI` with the `OpenAI` client for OpenAI and Azure v1, and
   Microsoft Agent Framework for the optional agent capabilities; no `Azure.AI.OpenAI` dependency and
   no waiting condition.

Spikes before building (no owner input, recorded as ADRs): the Marten two-store spike (I.4); the
RabbitMQ topology spike under ADR 0006; a check whether Wolverine already propagates W3C trace
context into envelopes. *Resolved, ADR 0009 (2026-10-07): it writes and restores `traceparent` on the
local path; #47 adds only the Operation activity's placement, `tracestate` and a parent fallback.*

### I.7 Recommended plan after Wave 1

Wave 1 is merged and the active [Phase 5 packaging and releases](#phase-5-packaging-and-releases) scope is complete.
Use one spec per wave: **Wave 2** — paging, one JSON setup, Web
defaults, Web.OpenApi, Core, Security and JwtBearer, request idempotency, Caching, scheduling,
Observability, the HttpClient handler, the versioning rule, the RabbitMQ transport after its spike,
and the template's first two compositions; **Wave 3** — Marten after its spike, multi-tenancy, AI,
Gateway, Grpc, Realtime, Storage, Email, number sequences, the reporting seam, Aspire, the saga; **Wave 4** — the template's service composition and the sample agent polished for the
skill. Every ticket follows section H. Wave 1 stays as built.
