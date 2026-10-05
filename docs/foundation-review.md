# Foundation kit: independent review and plan

Status: draft 6, 2026-10-04 (five Codex review rounds and the owner's decisions applied; see `foundation-review-log.md`). Analysis only — nothing was built, restored, run or modified in any
inspected repository. The kit is named **PouryaKit**. Every API shown under the `PouryaKit.*` prefix is
**proposed**, not existing CloudNativeKit/Woo functionality.

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
assemblies is not verified. Phase 0 checks it; default is the owned dispatcher.

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

### Allowed references

| Package | References | Third-party |
|---|---|---|
| Domain | — | none |
| Cqrs | Domain | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Logging.Abstractions` |
| Validation | Cqrs | FluentValidation |
| Modularity | Cqrs | `Microsoft.Extensions.Hosting.Abstractions` |
| Persistence.EFCore | Cqrs | EF Core Relational |
| Persistence.Postgres | Persistence.EFCore | Npgsql EF provider, EFCore.NamingConventions |
| Persistence.Dapper | Domain | Dapper, Npgsql (ADO.NET driver only, no EF) |
| Messaging | Domain (handlers return `Result`) | none |
| Messaging.Wolverine | Cqrs, Messaging | WolverineFx |
| Messaging.Wolverine.EFCore | Messaging.Wolverine, Persistence.EFCore | WolverineFx.EntityFrameworkCore, WolverineFx.Postgresql |
| Web | Cqrs | ASP.NET Core framework reference |
| Testing | Modularity, Persistence.Postgres, Messaging.Wolverine.EFCore | Testcontainers, Respawn, xUnit v3 |

Checks against your rules: Dapper does not reference EF. Postgres does not reference Wolverine. Web
does not reference Modularity. Nothing chooses an identity provider. There is no `Core`/`Utils`.
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
  Nothing mutates exported fields after step 3.
- **`IUnitOfWork` lives in `PouryaKit.Cqrs`**, because the pipeline that calls it lives there. That removes
  `Persistence.Abstractions`, and lets a single-module vertical-slice API use Cqrs + EFCore without
  Modularity.
- **`PouryaKit.Persistence.Dapper` is small on purpose.** It exists so Dapper never arrives through EF.
- **Security is not a package in v1.** The actor is part of `OperationContext`; `PouryaKit.Web` fills it
  from `ClaimsPrincipal`; the application configures JwtBearer itself. Cybernod's Security package
  shows the risk of doing more: subscription claims and an admin bypass are baked into shared code
  (`CustomClaims.cs:26-66`, `FeatureAuthorizationHandler.cs:11-18`).

### Full target catalogue

The goal is a complete kit, not a minimal one. The 12 projects above are **wave 1** because every
other capability sits on top of the execution core, and that core is the part no source gets right.
Waves are a build order, not a cut list. A capability becomes a package only where the kit adds
behaviour or a convention of its own; where .NET already ships the capability, the kit ships a
registration recipe inside the template instead of a wrapper.

| Capability | Form | Wave | Best source | Kit's own contribution |
|---|---|---|---|---|
| Domain, CQRS, validation, modularity, persistence, messaging, web, testing | 12 packages above | 1 | section B | the execution core |
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

**Phase 5 — packaging** (needs 4)
- Central package management, MinVer, Source Link, `EnablePackageValidation`, `THIRD-PARTY-NOTICES`
  packed into each nupkg, MIT licence. Publish `0.x` prereleases to GitHub Packages first; nuget.org
  only unlists, never deletes.
- Done when: a test asserts each package's transitive closure (row 17) — e.g. Dapper has no EF,
  Postgres has no Wolverine, Web has no Modularity, `PouryaKit.Messaging.Wolverine` has no EF; a consumer of each profile builds from packages.

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
- Wolverine internals: per-retry scope is still unverified (S2). Send timing without an enrolled
  transaction and queue naming defaults were observed in Spike S1 against WolverineFx 6.46.0; see ADR 0004.
- Whether Wolverine can route one message to several kit-registered consumers with independent ack
  without app-specific handler classes (S2).
- Whether `Mediator` 3.x handles `internal` handlers across assemblies (S3).
- How Lucky Penny §4.2.e.3 would be applied to your case is a legal question; the text is clear enough
  that I would not build on it.

**Needs build / database / runtime verification:** everything in Phases 0–5. Nothing in this
document has been compiled.

**Decided by the owner (2026-10-04):**
1. **One host container.** Modules keep their own composition root, gateway, operation scope,
   DbContext and schema; no private `ServiceProvider` per module.
2. **Adoption rule.** Take the best implementation from any shared source and customise it, but the
   result must be one consistent design — one implementation per concern, one naming scheme. The
   kit's own docs and provenance records cite upstream repository, commit and repo-relative path,
   never a local machine path. Ultrabusiness ownership is unconfirmed, so it is adopted at pattern
   level (reimplemented, with its test scenarios ported); the public MIT samples may be copied with
   attribution.
3. **Private first, public later.** `0.x` on GitHub Packages, then nuget.org under MIT.
4. **Monolith and modular monolith first, broker-ready from day one.** Messaging contracts, wire
   names, headers, routing declarations and receipts are transport-neutral in wave 1; the RabbitMQ
   transport package moves up to wave 2.

5. **Name and prefix: `PouryaKit`.**
6. **.NET 10 only**, latest C# language version, current .NET idioms throughout.
7. The "no local files" rule is confirmed as a docs/provenance rule.
8. The owner authorised finalising this plan and splitting it into phases worked through the
   repo's skills. Building has not started.

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

No test-first default. Each ticket goes through:

1. **Implement** — Opus builds from the ticket.
2. **Focused tests** — tests for the behaviour that matters; no redundant coverage.
3. **Review, sized to the ticket** (owner, 2026-10-04) — two independent reviews in parallel, a Fable
   subagent and Codex (GPT-6 Astra, high), for a ticket that claims a runtime guarantee or changes a
   public API of the kit; one reviewer for any other ticket; none for a clear, small change.
4. **One fix round, then a re-score** of the accepted findings only. A new full review only after a
   high finding was fixed; a remaining medium or low finding gets a recorded disposition.
5. **Runtime proof** where the ticket claims a runtime guarantee (PostgreSQL in Testcontainers).
6. **Docs** — update documentation and C4 diagrams; run `/archify-review`.
7. **AI setup** — add, update, simplify or remove the kit's skills, rules and agent docs, following
   `/skill-creator` and `/writing-for-agents`.

Completion gates: relevant tests green · no open high finding and a recorded disposition for the rest · runtime proof
where applicable · docs in sync · AI guidance in sync.

### Found incidentally in Cybernod (not part of this plan)

- Single-scan delete has no ownership check in three modules (`AggregateRootByIdSpec.cs:14`).
- `GET identity-module/subscription/scan-availability/by-user` is anonymous.
- Unknown exceptions return their message to clients (`UnknownExceptionProblemDetails.cs:26`).
- `isDev && isTest` in `Program.cs:80,124,158` is never true.
- A failed `Result` commits today (`SubmitInternalScanResultsCommandHandler.cs:40,52-55`).

---

## Recommended plan in one paragraph

Build an owned kit on .NET 10 with no mediator dependency, 12 packages in the first wave and the
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
