---
status: accepted
---

# The Unit of Work commits through Wolverine's EF outbox inside its own transaction

Spike S1 (ticket #2) ran the commit design of ADR 0003 against WolverineFx 6.46.0 and PostgreSQL. The Kit's
Unit of Work commits business rows and Outbox rows atomically for two Modules' DbContexts in one Host
container through one Wolverine message store. It does so by owning an explicit database transaction:
begin, run the Operation, `SaveChangesAsync`, commit, and only then `FlushOutgoingMessagesAsync` on
`IDbContextOutbox<TDbContext>`. Module DbContexts carry no Wolverine mapping. The EF mode is `Eager`.
Nothing observed contradicts ADR 0001–0003 or section G.

One gap remains for ticket #15. If the commit succeeds but the flush never happens, and the Host stays
alive, the Notification is delayed rather than lost. It is delivered when the Host stops. If
`InboxStaleTime` is set, it is delivered after `InboxStaleTime` plus up to two recovery polls. That remedy
has a cost: a handler that runs longer than the stale time is handed out again on every recovery cycle. See
"Committed but not flushed" below.

## Evidence

The Spike code is `spikes/s1-atomic-commit/` at commit `82b0b8fd8a9c25bf5fb0ca17e4fef85072116fc3`, reachable
through tag `spike/s1-atomic-commit`. It is not merged. Earlier rounds of tests were committed at
`64e2b16` and `72fefde`.

The tests were run on 2026-10-05 with `dotnet test spikes/s1-atomic-commit` (.NET SDK 10.0.401, Microsoft
Testing Platform), against `postgres:18.6` in Testcontainers 4.15.0. 38 of 38 tests passed.

- Each test gets a fresh database.
- Catalog (`catalog` schema) and Inventory (`inventory` schema) each have their own DbContext.
- Each Module publishes one Notification to a durable local queue, and the handler records every delivery in
  `spike.deliveries`.
- No EF retrying execution strategy is configured anywhere.

| Point | Deciding test (class.method, all cases) | Observed |
|---|---|---|
| Atomic commit, each Module | `AtomicCommitTests.Commit_writes_business_row_and_envelope_row_in_one_transaction` (Catalog and Inventory × Raw and mapped × Eager and Lightweight) | After `SaveChangesAsync` and before commit, the Operation's own connection sees the business row and the envelope row, and a second connection sees neither. After commit both rows exist with the same `pg_xact_commit_timestamp`, which means one commit. The Notification is then delivered. |
| Rollback after save | `AtomicCommitTests.Exception_after_save_rolls_back_business_row_and_envelope_row` | No business row, no envelope row, and no delivery within 3 s. |
| Failed Result | `AtomicCommitTests.Failed_outcome_after_publish_leaves_no_business_row_and_no_envelope_row` | No business row, no envelope row, and no delivery within 3 s. In the unmapped path, publish writes the envelope row inside the transaction before the Result is checked. That part is inferred from source (`EfCoreEnvelopeTransaction.cs`); this test does not observe it. |
| Failed save | `AtomicCommitTests.Failed_save_leaves_no_envelope_row` | A duplicate key in `SaveChangesAsync` leaves no envelope row and no delivery. |
| Crash after commit | `CrashRecoveryTests.Message_committed_before_the_process_is_killed_is_delivered_after_restart` | A child process (`dotnet S1.Host.dll`) commits, prints `COMMITTED` before flushing, and is killed with `Process.Kill` (SIGKILL). Until the flush nothing is delivered: the envelope row stays `Incoming`, owned by the dead node. A restarted Host delivers it: in 4.9 s in Balanced mode with shortened timers, in **74.6 s in Balanced mode with Wolverine's default timers**, and in 3.9 s in Solo mode. |
| Crash during handling | `CrashRecoveryTests.Message_whose_handler_was_running_when_the_process_was_killed_is_handled_again_after_restart` | The handler's side effect was already written when the process was killed. After restart the handler runs again, so delivery is at-least-once. |
| EF mode | `EfModeTests.Middleware_handler_effect_and_inbox_mark_share_a_transaction_only_in_eager_mode` (Eager and Lightweight × Raw and mapped) | For a `[Transactional]` Wolverine handler, `Eager` opens a transaction before the handler runs, and the effect and the inbox `Handled` mark commit together. `Lightweight` opens no transaction, so the effect commits alone and the mark commits about 1 ms later in a separate transaction. For the owned Unit of Work the mode makes no difference: every case of the first row passes in both modes. |
| Committed, not flushed, Host alive | `StrandedNotificationTests.Notification_committed_but_not_flushed_waits_on_a_live_host_until_the_host_stops` | See "Committed but not flushed" below. |
| Remedy: `InboxStaleTime` | `StrandedNotificationTests.Notification_committed_but_not_flushed_is_delivered_on_the_same_live_host_when_inbox_stale_time_is_set` | See below. |
| Cost of the remedy | `StrandedNotificationTests.Inbox_stale_time_shorter_than_a_running_handler_hands_the_notification_out_on_every_recovery_cycle` | See below. |
| One outbox instance, one attempt | `StrandedNotificationTests.An_outbox_instance_reused_after_a_rollback_sends_the_rolled_back_notification_with_the_next_commit`, `StrandedNotificationTests.A_second_commit_on_an_outbox_instance_that_already_flushed_is_committed_but_not_sent`, `ObservationTests.Flushing_the_outbox_after_a_rollback_delivers_a_message_whose_business_row_was_rolled_back` | See below. |
| One store, two DbContexts | `ObservationTests.One_message_store_in_the_wolverine_schema_serves_both_module_DbContexts` | See below. |
| Send without the outbox | `ObservationTests.Publish_through_the_plain_message_bus_escapes_the_unit_of_work_and_is_delivered_after_rollback` | See below. |
| Mapping schema trap | `ObservationTests.Envelope_storage_mapped_without_the_store_schema_fails_the_commit` (Catalog, Inventory) | See below. |

Source reading at `jasperfx/wolverine` tag `V6.46.0` (commit `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`)
explains these results:

- `src/Persistence/Wolverine.EntityFrameworkCore/Internals/EfCoreEnvelopeTransaction.cs`: when a DbContext is
  not mapped, envelope rows are written with SQL on the DbContext's own connection and transaction. If no
  transaction is open, it begins one.
- `src/Wolverine/Persistence/Durability/IEnvelopeTransaction.cs`: any `local://` destination is persisted as
  an *incoming* envelope.
- `src/Persistence/Wolverine.EntityFrameworkCore/WolverineEntityCoreExtensions.cs`: the mode only sets
  `EFCorePersistenceFrameProvider.DefaultMode`, which applies to generated handler middleware and nothing else.

## Pinned versions and licences

Versions and licences are as published on nuget.org and read from the restored nuspecs on 2026-10-05.

| Package | Version | Licence |
|---|---|---|
| WolverineFx | 6.46.0 (current stable; has `net10.0` target) | MIT |
| WolverineFx.EntityFrameworkCore | 6.46.0 (requires EF Core ≥ 10.0.4 on `net10.0`) | MIT |
| WolverineFx.Postgresql | 6.46.0 | MIT |
| WolverineFx.RuntimeCompilation | 6.46.0 | MIT |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 (EF Core resolves to 10.0.4) | PostgreSQL |
| JasperFx / Weasel.* | 2.80.2 / 9.41.0 (transitive) | MIT |

The Host's full closure is 90 packages: 83 MIT, 3 BSD-3-Clause (NetTopologySuite, NetTopologySuite.IO.PostGis,
Polly.Core), 3 PostgreSQL (the Npgsql packages) and 1 Apache-2.0 (NewId). None is reciprocal or commercial,
so no `stop_if` applies.

The closure contains Roslyn, and it arrives from two sources:

- `Microsoft.EntityFrameworkCore.Design` 10.0.4 comes through WolverineFx.EntityFrameworkCore, as plan
  section C expects. It already depends on `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.CSharp.Workspaces`
  and `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.0.0.
- WolverineFx.RuntimeCompilation, through `JasperFx.RuntimeCompiler` 5.0.0, adds the `Microsoft.CodeAnalysis`
  meta-package, `Microsoft.CodeAnalysis.Scripting` and the VisualBasic packages.

Pre-generated code (`TypeLoadMode.Static`) therefore does not remove Roslyn from a Host that uses the EF
outbox. The argument about keeping the closure small matters only for the transport-neutral
`PouryaKit.Messaging.Wolverine` package.

**Wolverine 6 no longer ships its runtime compiler.** Without `WolverineFx.RuntimeCompilation`, the Host fails
at start with "Wolverine is running in TypeLoadMode.Dynamic … but no IAssemblyGenerator (Roslyn) is
registered". This was observed by removing the reference.

## The registration and commit code that worked

Registration in the Host (`S1.Host/SpikeHost.cs`). The policy block is reduced from `meysamhadeli/woo` at
`149a3cf`, `src/Wolverine/Extensions.cs`.

```csharp
services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(dataSource));   // no EnableRetryOnFailure
services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(dataSource)); // no Wolverine mapping in either model

builder.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(SpikeHost).Assembly);           // handlers live outside the entry assembly
    opts.PersistMessagesWithPostgresql(connectionString);                // one store per Host; schema defaults to "wolverine"
    opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager); // also registers IDbContextOutbox<T>
    opts.Policies.UseDurableLocalQueues();
});
```

Commit, abridged from `S1.Host/SpikeUnitOfWork.cs`. `outbox` is the scope's `IDbContextOutbox<TDbContext>`:

```csharp
var db = outbox.DbContext;
await using var tx = await db.Database.BeginTransactionAsync(ct);
try
{
    var outcome = await operation(db, outbox);   // Spike stand-in for: handler, Domain Event drain, then the
                                                 // Unit of Work itself calls outbox.PublishAsync per Notification
    if (outcome == Outcome.Failed) { await tx.RollbackAsync(ct); return Outcome.Failed; } // no flush
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
}
catch
{
    await tx.RollbackAsync(CancellationToken.None);   // and never flush after this
    throw;
}
await outbox.FlushOutgoingMessagesAsync();         // only after a successful commit
```

## Observed behaviour previously listed as inferred (plan section F)

Per-retry scope, also listed in section F, was not examined and remains unverified (S2). *Answered by ADR 0006, "What
this refines in ADR 0004": each attempt runs in a new scope the Kit opens.*

- **Send timing with the outbox.** Publishing writes the envelope row at once, inside the Unit of Work's
  transaction. With a Wolverine-mapped DbContext it is written at `SaveChangesAsync` instead. Nothing is sent
  until `FlushOutgoingMessagesAsync`.
- **Send timing without an enrolled transaction.** `IMessageBus.PublishAsync` stores the envelope on its own
  connection and sends it at once. The handler had already run while the Operation's transaction was still
  open, and the rollback that followed could not undo it.
- **One `IDbContextOutbox<T>` instance, which means one scope, serves exactly one commit attempt.** Three
  tests observe this:
  - Rollback removes the envelope row, but the instance keeps the envelope in its outstanding list. A flush
    after the rollback delivered a Notification whose business row does not exist.
  - On a reused instance, the rolled-back Notification was delivered with the next successful commit's
    flush. This happened even with EF's change tracker cleared in between.
  - `MultiFlushMode` defaults to `OnlyOnce` (`src/Wolverine/Runtime/MessageContext.cs:85`). A second commit on
    an instance that has already flushed is committed, but it is never sent: the row stays `Incoming` and is
    owned by the live node.
- **Committed but not flushed.** Any failure after the server commits skips the flush. That includes an
  exception between commit and flush, and a lost commit acknowledgement, which looks like a failed commit to
  the Unit of Work. Wolverine's flush also logs and discards a send failure
  (`src/Wolverine/Runtime/MessageContext.cs:251-261`).
  - **The row is stranded while its node lives.** The row stays `Incoming`, owned by a live node. Inbox
    recovery selects only `owner_id = 0`
    (`src/Persistence/Wolverine.RDBMS/Durability/CheckRecoverableIncomingMessagesOperation.cs:34`). The orphan
    sweep releases only owners missing from the node table
    (`src/Persistence/Wolverine.RDBMS/Durability/ReleaseOrphanedMessagesCommand.cs:171`). Observed: no
    delivery within 20 s with 1 s timers.
  - **Stopping the Host releases it.** A graceful stop set `owner_id = 0`, and the next Host delivered the
    Notification. It is delayed, not lost.
  - **Remedy: `InboxStaleTime`.** When set, the durability agent sets `owner_id = 0` on every inbox row with
    `owner_id != 0` whose `timestamp` is older than the stale time
    (`src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs:348`, `BumpStaleIncomingEnvelopesOperation.cs:27`;
    the default is null, `src/Wolverine/DurabilitySettings.cs:234`). With 5 s, the stranded Notification was
    delivered on the same live Host 6.8 s after commit, exactly once.
  - **How long it takes.** The release and the inbox recovery both run on the recovery timer, whose period
    is `ScheduledJobPollingTime` (`src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs:98-102`; the default is
    5 s). The delay is therefore `InboxStaleTime` plus up to two recovery polls, not "within
    `InboxStaleTime`". The observed 6.8 s for a 5 s stale time with 1 s polls fits this.
  - **Cost of the remedy: repeated, concurrent hand-outs.** The release does not distinguish a stranded row
    from one still in flight. It filters only on `owner_id != 0` and `timestamp <= cutoff`
    (`BumpStaleIncomingEnvelopesOperation.cs:27`). The `timestamp` column is set by `now()` at insert
    (`src/Persistence/Wolverine.Postgresql/Schema/IncomingEnvelopeTable.cs:34-36`), and nothing updates it. So
    an envelope whose handler outlasts the stale time is released again on every recovery cycle, until one
    run marks it handled.
    - Observed, with a 3 s stale time, 1 s polls and a 15 s handler: the second hand-out came 4.3–4.4 s after
      commit, and there were 6 hand-outs within 14 s. All of them started before the first run could end, so
      they ran concurrently.
    - With the default 5 s `ScheduledJobPollingTime`, the repetition interval would be about one hand-out
      per 5 s. That figure is derived from the timer code, not measured.
    - Consequences: the stale time must exceed the longest queue wait plus handling time the Host expects,
      and Receipts (G.2) must hold under concurrent duplicate hand-outs. Setting it also makes Wolverine add
      the `timestamp` column to the inbox table.
  - **Woo's setting.** Woo sets `InboxStaleTime` to 5 minutes (`meysamhadeli/woo` `149a3cf`,
    `src/Wolverine/Extensions.cs:45`). With that value, a stranded Notification waits about 5 minutes plus up
    to two recovery polls on a live Host. Any handler running longer than 5 minutes is handed out again on
    every recovery cycle until a run completes.
- **Queue naming defaults.** There is one durable local queue per Notification type (CLR type), named after
  the type's full name in lower case: `local://s1.productcreated/` and `local://s1.stockitemcreated/`.
- **Where envelopes are stored.** Envelopes are stored in `wolverine.wolverine_incoming_envelopes`; the
  outgoing table stays empty for local queues. The schema `wolverine` is the default, and no Wolverine table
  appears in a Module schema. The row has status `Incoming` and `owner_id` set to the writing node's number,
  not `0`.
- **One message store serves two DbContexts.** One `PersistMessagesWithPostgresql` call serves both, provided
  every DbContext connects to the store's database. The unmapped path writes with the store's own
  schema-qualified SQL. The mapped path works only with `MapWolverineEnvelopeStorage("wolverine")`. Mapping
  without the schema puts the tables into the Module's schema, and the commit fails on the missing table
  (`catalog.wolverine_incoming_envelopes`).
- **EF savepoint.** In the unmapped path the business row and the envelope row have different `xmin` values,
  because EF Core runs `SaveChangesAsync` inside an automatic savepoint. They are still one commit. Use the
  commit timestamp, not `xmin`, to prove "same transaction". `pg_xact_commit_timestamp` returns NULL unless
  the server runs with `track_commit_timestamp=on`; the Spike's container sets it.
- **Recovery after a crash.** A node killed after commit leaves rows owned by its node number. In Balanced
  mode they are released only after the node is judged stale and the orphan sweep runs
  (`src/Persistence/Wolverine.RDBMS/Durability/ReleaseOrphanedMessagesCommand.cs`). That took 74.6 s with
  defaults. In Solo mode the restarted node takes the same number, 1, and delivered them 3.9 s after it
  started.

## Invariants for permanent tests

| Invariant | Seam | Owner |
|---|---|---|
| A failed Result, a failure after save, or a failed save leaves no business row, no Outbox row and no delivery, for each Module | Gateway | #15 |
| A successful Operation's business rows and Notifications become visible together, and the Notification is delivered | Gateway | #15 |
| A Notification committed before the Host dies is delivered after restart (the test needs a stop point between commit and flush) | Gateway | #15 |
| A Notification committed but not flushed on a live Host is delivered without a restart. The remedy the evidence supports is setting `InboxStaleTime` from a Kit setting; Woo's 5 minutes is a starting value. Its cost: delivery is delayed by `InboxStaleTime` plus up to two recovery polls; a handler running longer than it is handed out again, concurrently, on every recovery cycle until one run completes, so `InboxStaleTime` must exceed the longest handler run (including queue wait) the Host expects; and the inbox table gains a `timestamp` column. Rejected: retrying the flush, because the flush swallows send failures and a lost commit acknowledgement hides the commit; and accepting delivery at the next restart, which is unbounded on a long-lived Host. | Gateway | #15 |
| One `IDbContextOutbox<T>` instance, one scope, serves exactly one commit attempt: never reused after a rollback, never asked to commit twice | Gateway | #15 |
| Feature code never reaches `IMessageBus`, `IMessageContext` or `IDbContextOutbox<>`; only the Unit of Work publishes and flushes | Structure | #15 |
| Module DbContexts do not map Wolverine envelope storage (`MapWolverineEnvelopeStorage`, `AddDbContextWithWolverineIntegration`) | Structure | #15 |
| A Notification or Integration Event applies its effect once, through the handler's Receipt. This must hold for a re-run after a crash during handling, and also under several concurrent duplicate hand-outs, such as those `InboxStaleTime` produces when a handler outlasts it | Gateway | #16 |

## Defaults chosen

On 2026-10-05 the owner asked for these defaults to be settled by what the reference projects and Wolverine
recommend across a monolith, a modular monolith and microservices. The owner can overturn any of them.
Wolverine documents are cited at tag `V6.46.0`.

1. **Durability mode.** A Kit Host keeps Wolverine's default, Balanced, because it is the only mode that is
   safe with more than one replica.
   - A Host may opt into Solo and set the node timers.
   - The Testing package defaults to Solo (`services.RunWolverineInSoloMode()`, `docs/guide/testing.md`).
   - A test that needs Balanced mode, such as one that kills a node, opts out of Solo and uses short timers.
   - Wolverine recommends Solo for development and frequent restarts
     (`docs/guide/durability/leadership-and-troubleshooting.md`, "Solo Mode").
   - Evidence: recovery took 74.6 s in Balanced mode with default timers and 3.9 s in Solo mode.
2. **Runtime code generation.** No Kit runtime package references `WolverineFx.RuntimeCompilation`. The Host
   references it, following `docs/guide/codegen.md`: dynamic code in development, pre-generated code in
   production, and a package reference that can be made conditional on the build configuration.
   - The Testing package may reference it.
   - Wave 1's Proving Sample Host runs dynamic. Pre-generation belongs with the project template (plan
     Phase 6).
   - As corrected above, Roslyn still reaches any Host that uses the EF outbox through
     `Microsoft.EntityFrameworkCore.Design`. Keeping Roslyn out is a property of the transport-neutral
     `PouryaKit.Messaging.Wolverine` package only.
3. **Store schema.** The schema is set explicitly from one Kit setting, default `wolverine`, and is
   configurable per Host.
   - `docs/tutorials/modular-monolith.md` sets `Durability.MessageStorageSchemaName` so that all modules share
     one inbox and outbox.
   - `docs/guide/durability/postgresql.md` says separate logical services that share a database should each
     use their own schema name.
4. **Outbox and Inbox wording.** A Module owns its Outbox and Inbox logically, on its Host's one shared
   physical store. `GLOSSARY.md` now defines each as a Module's share of its Host's durable message store.
   G.2 is unchanged.

## Notes for S2

- Wolverine's inbox mark is per envelope, not per handler. Only `Eager` middleware commits it with a
  handler's effect. A crash between effect and mark re-runs the handler, as the second crash test showed, so
  independent retry per handler still needs the Kit's Receipt (G.2).
- `DurabilitySettings.MessageIdentity` defaults to `IdOnly`. Its source comment recommends `IdAndDestination`
  for modular monoliths that receive one envelope on several endpoints, which bears on separated per-handler
  queues. This was not tested.
- Each Notification type had one handler here. What one local queue does with several handlers of the same
  type was not tested. *Answered by ADR 0006, "What this refines in ADR 0004"; the Kit gives each subscription its
  own queue.*
- Retry and recovery tests need short durability timers or Solo mode. The defaults take more than a minute
  to recover a dead node's envelopes. An `InboxStaleTime` shorter than a handler's run hands the envelope out
  again on every recovery cycle while the handler is still running.
- Per-retry scope (plan section F) was not examined. *Answered by ADR 0006, as above.*
- Woo's block also sets `ConventionalLocalRoutingIsAdditive`, `UseDurableInboxOnAllListeners`,
  `UseDurableOutboxOnAllSendingEndpoints`, `RetryWithCooldown` and the inbox and outbox stale times. S1 used
  none of them, except `InboxStaleTime` in the stranded-Notification tests.

## Considered Options

- **`IDbContextOutbox<T>.SaveChangesAndFlushMessagesAsync`.** Rejected. It saves, commits whatever transaction
  is open and flushes in one call, so the commit and the "no flush after rollback" rule leave the Unit of
  Work (ADR 0003).
- **Wolverine-mapped DbContexts** (`MapWolverineEnvelopeStorage("wolverine")`). This passes every test, but
  rejected. It puts Wolverine entities into every Module's model, makes each DbContext repeat the store
  schema (a wrong one fails the commit), and is a second way to do the same thing.
- **`Lightweight` mode.** It gives the same result for the owned Unit of Work, but rejected. For any handler
  that uses Wolverine's EF middleware, it commits the effect and the inbox mark separately.
- **Solo durability mode as the Host default.** It recovers fastest, but it assumes a single node. It is the
  default for tests only (Defaults chosen, 1).

## Consequences

- The Unit of Work always holds an explicit transaction. That is incompatible with an EF retrying execution
  strategy, which matches the decision to run none. A whole-Operation retry starts a new scope (ADR 0003).
- The Host references `WolverineFx.RuntimeCompilation` while it runs dynamic code (Defaults chosen, 2). With
  the EF outbox, Roslyn is in the Host's closure either way.
- Physically, the Outbox is one Wolverine store per Host. Each Module's Notifications are distinguished by
  CLR type and local queue.
- Disposing a Host logs `ObjectDisposedException` errors from Wolverine's database batcher. They are noise,
  and test log assertions should tolerate them.

## Amendment (2026-10-05, ticket #17)

The decision above stands. Ticket #17 extended the cost of its `InboxStaleTime` remedy.

- **The stale time covers the whole retry schedule.** The invariant owned by #15 says `InboxStaleTime` must exceed
  the longest handler run, queue wait included. The release compares the insert time, and a stored retry keeps it, so
  a delivery that fails ages through its retry delays as well. `InboxStaleTime` must exceed the sum of the retry
  delays plus every attempt's run, queue wait included: an attempt that runs after it is handed out again while it
  runs, and the Receipt keeps the effect to one (`InboxStaleTimeTests`). The evidence, and the default retry delays
  shortened to fit, are in ADR 0006's amendment for ticket #17.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. Ticket #15 met its one-instance invariant by another route than the text names.

- **A new outbox for each commit attempt.** The decision and the invariant "One `IDbContextOutbox<T>` instance, one
  scope, serves exactly one commit attempt" name the scope's `IDbContextOutbox<TDbContext>`. The Unit of Work's Outbox
  builds a new `DbContextOutbox` for each commit attempt instead and enrolls the Unit of Work's DbContext in it, so an
  instance that saw a rollback is never flushed and the invariant holds by construction
  (`src/PouryaKit.Messaging.Wolverine.EFCore/WolverineOutbox.cs`). A failed flush after the commit is logged and not
  reported: the change committed, and a failure would invite the caller to make it twice; the store recovers the
  envelopes after `InboxStaleTime`.
- `DurabilitySettings.MessageIdentity` (Notes for S2) is still open; it belongs with brokers in Wave 2 (ADR 0006,
  Consequences).

## Amendment (2026-10-07, ticket #58: code generation for production)

The decision above stands. Ticket #58 built "Defaults chosen", 2, as Wolverine's code generation guide recommends for
the pinned version (`JasperFx/wolverine` commit `dc63f46`, `docs/guide/codegen.md:188-211,235-260`; its sample
`JasperFx/CritterStackSamples` commit `76ce8e2`, `CqrsMinimalApi/`).

- **The mode is the Kit's default, set once.** `UsePouryaKitWolverine` of `PouryaKit.Messaging.Wolverine` sets the
  guide's production profile with `CritterStackDefaults`: `TypeLoadMode.Dynamic` in Development, `TypeLoadMode.Static`
  with `AssertAllPreGeneratedTypesExist` in every other environment. The Host's `configure` hook can override it; the
  EF Core overload gained the same optional `configure` parameter, applied after the Kit's store settings. `Auto` is not
  used. In Static mode Wolverine attaches every handler chain's generated type as it starts and throws
  `MissingPreBuiltTypesException` for a missing one (`HandlerGraph.AssertPreBuiltTypesExist`,
  `src/Wolverine/Runtime/Handlers/HandlerGraph.PreBuiltTypes.cs` at tag `V6.46.0`, commit
  `1f22835a2b3e32dbf4b75c7a5273622ee74a4929`;
  `CodeGenerationTests.A_host_outside_development_whose_handler_chain_has_no_generated_type_does_not_start`). The
  profile's assertion is JasperFx's environment check: `dotnet run -- check-env` also fails on a missing handler
  registry, which the start only warns about (observed on the sample Host).
- **The application assembly is the Host's.** Wolverine and JasperFx 2.80.2 take the first assembly on the stack
  outside their own as the application assembly, and Static mode loads generated types from it alone. Called through the
  Kit, that was `PouryaKit.Messaging.Wolverine` (`dotnet run -- describe` on the sample Host), never the Host into which
  `codegen write` writes. `UsePouryaKitWolverine` therefore sets `WolverineOptions.ApplicationAssembly` to the entry
  assembly: the Host in `codegen write`, in a published Host and in the Proving Sample's child process; the test
  assembly, which holds no generated code, in a test process.
- **Every Host owns the command line.** The Proving Sample's two Hosts and both template Hosts end with
  `return await app.RunJasperFxCommands(args);`, so `dotnet run -- codegen write` writes the Host's generated code into
  its `Internal/Generated` and `codegen preview` prints it. `write` builds the Host without starting it and compiles
  nothing, so it needs neither a database nor the runtime compiler. The Host's arguments are JasperFx commands; its
  configuration comes from settings files, user secrets or the environment. A test that hosts `Program` through
  `WebApplicationFactory` sets `JasperFxEnvironment.AutoStartHost` (Wolverine's integration testing guide).
- **The compiler is a Debug reference.** Each Host references `WolverineFx.RuntimeCompilation` in an
  `<ItemGroup Condition="'$(Configuration)' == 'Debug'">`, the guide's form, so a Release publish carries neither
  `Wolverine.RuntimeCompilation.dll` nor `JasperFx.RuntimeCompiler.dll`. A restore that evaluated Debug still lists it,
  so a publish restores for Release. Roslyn itself stays in a Host with the EF Core outbox, through
  `Microsoft.EntityFrameworkCore.Design`, as "Pinned versions and licences" says; the sample's ban of
  `Microsoft.CodeAnalysis` in the publish is not copied.
- **The Testing package takes the exception this record allows.** `PouryaKit.Testing` references the package, so a
  test Host can run Development from any build, Release included; a generated solution's test project, which does not
  reference `PouryaKit.Testing`, references it directly. No other Kit package references it (`PackageGraphTests`).
- **What `codegen write` writes for a Kit Host.** The Kit's deliveries are `AddMessageHandler` handlers, which have no
  generated code, so a Kit Host gets `Internal/Generated/WolverineHandlers/GeneratedHandlerRegistry.cs` alone, an empty
  handler registry; without it, Static mode logs that it falls back to a scan and starts. A handler chain the
  application adds through the `configure` hook is what needs the step. The Kit commits no generated code; the
  templates document the step, and a generated solution commits its own, as the guide's sample does.
- **Message storage is still built at startup.** The guide's sample also sets `Production.ResourceAutoCreate` to
  `AutoCreate.None`; the Kit does not, since nothing else creates Wolverine's tables for a Kit Host.
- **Proof.** `scripts/packaging/validate.py` runs `codegen write` on each Host, the two generated ones and the Proving
  Sample's two in its copied sample, publishes it in Release and checks the publish (`check_production_publish`), all on
  the consumers' copies, so the checkout gets no generated file. The Proving Sample's child-process test runs
  `Inventory.Host` in Production, Static, from the tests' build. Each published generated Host was started once by hand
  in Production against PostgreSQL (`docs/acceptance.md`).

## Amendment (2026-10-08, ticket #63: the store's database checked when the Host is built)

The decision above stands. "One message store serves two DbContexts" holds "provided every DbContext connects to the
store's database", and until ticket #63 nothing checked that proviso: it was a deployment rule only.

- **What a DbContext on another database did.** #54's probe of 2026-10-08 (`MessageStoreDatabaseTests` at commit
  `3861dc9`, recorded in `docs/acceptance.md`) confirmed the unmapped path above: Wolverine writes each envelope on the
  Unit of Work's own connection and transaction, in the store's schema, so into the DbContext's database. With no store
  tables there, the first Operation that wrote to the Outbox failed with `42P01` and nothing committed. With a copy of the
  store's tables there, it committed into that copy, the handlers ran once from the in-memory flush, and the rows stayed
  `Incoming` where the store never reads them, with nothing logged at Warning or above.
- **The check.** `UsePouryaKitWolverine(connectionName)` registers the composition check `MessageStoreDatabaseCheck`. When
  the Host's container is built, it compares the connection string Wolverine was given with the connection of each
  DbContext `UsePostgres` records as a `DbContextConnection` (`PouryaKit.Persistence.EFCore`; since the Wave 2 final
  review, 2026-10-08, `AddPouryaKitEfCore` records every DbContext it makes a Unit of Work, so one the application
  registered itself is checked too, and `UsePostgres` passes it the connection name), the connection EF Core
  gives the DbContext itself, whatever configured it, a later `ConfigureDbContext` onto another string, a connection or
  an Npgsql data source included, on hosts, ports and database as Npgsql reads them, and fails the build with one line
  per DbContext on another database. It opens no connection, and two names for one database pass
  (`MessageStoreDatabaseTests`). A string that names neither a database nor a user, on either side, leaves the database
  to the client's user, so it fails the build too, asking for `Database`, or at least `Username` (orchestrator's decision
  of 2026-10-08, second review of #63).

## Amendment (2026-10-08, ticket #64: the `PouryaKit` prefix on two registrations)

The decision above stands. On 2026-10-08 the owner decided that the two registrations of the Kit without the `PouryaKit`
prefix take it before the first publication, with no alias and no forwarder: `UsePostgres<TDbContext>`
(`PouryaKit.Persistence.Postgres`) is now `UsePouryaKitPostgres<TDbContext>`, and `MapUniqueConstraint<TDbContext>`
(`PouryaKit.Persistence.EFCore`) is now `MapPouryaKitUniqueConstraint<TDbContext>`. Signatures and behaviour are
unchanged. The text above keeps the former names, as it was written: where it says `UsePostgres`, the code says
`UsePouryaKitPostgres`, and the line `MessageStoreDatabaseCheck` reports names the new call.

## Amendment (2026-10-09, ticket #75: WolverineFx 6.48.2)

The decision above stands. The five `WolverineFx*` pins move from 6.46.0 to 6.48.2: `WolverineFx`,
`WolverineFx.EntityFrameworkCore`, `WolverineFx.Postgresql`, `WolverineFx.RabbitMQ` (ADR 0006's #50 amendment) and
`WolverineFx.RuntimeCompilation`, in `Directory.Packages.props` and in each template's own, which `TemplateTests` keeps
equal to it. "Pinned versions and licences" stays as the record of what Spike S1 ran on, and the `V6.46.0` source
citations above stay as they were read.

- **Why.** In 6.46.0, after a RabbitMQ connection recovery, Wolverine's connection monitor rebuilt the broker agents in a
  plain loop: one `ReconnectedAsync` exception ended it, and that listener and every one after it kept a channel with no
  consumer while its status stayed `Accepting` and health stayed green. 6.48.0 rebuilds each agent on its own, retries a
  failed rebuild on a bounded schedule and tolerates tearing down a channel the client already closed
  (`JasperFx/wolverine` #4864, #4867). A Kit Host receives through exactly one durable broker listener.
- **Which release.** 6.48.2, as the ticket names it. The 6.48.1 notes fix the order in which a global partition slot's
  backlog resumes on its new owner; the 6.48.2 notes make the leader's agent assignment scale to tens of thousands of
  agents and add `DurabilitySettings.LeaderTakeoverHoldEvaluations` (default 1), which withholds first-time placements
  for one evaluation after a leader takeover, and stands down while no other node holds an assignment. The Kit uses no
  global partitioning and leaves the setting at its default. Each package is `MIT` with a `net10.0` asset; against
  6.46.0 their only dependency change is `JasperFx`, `JasperFx.Events` and `JasperFx.SourceGenerator` 2.80.2 to 2.81.0,
  `MIT`; `WolverineFx.RDBMS` and `WolverineFx.Newtonsoft` follow to 6.48.2, and restore resolves no other new version
  (Weasel stays at 9.41.0).
- **Proof.** Compatibility: the build has no warning, the whole suite passes on 6.48.2, and `validate.py` passes
  (`docs/acceptance.md`, "Pre-Wave 3 WolverineFx 6.48.2, ticket #75"). The repair itself is upstream's, proven by its
  own tests. `RabbitMqDeliveryTests.A_host_whose_broker_connections_drop_while_it_runs_consumes_its_queue_again_and_a_publication_committed_after_arrives`
  closes every connection of the test's vhost from the broker while both Hosts run: the Inventory Host's queue loses its
  consumer, gets it back, and a publication committed afterwards arrives. It passes on 6.46.0 as well, so it is not a
  regression test for #4867: a single broker's forced close makes no rebuild throw, and the defect needs one that does.
- **6.48.3 (2026-10-09, #75 follow-up).** The seven pins move on to 6.48.3, published the same day at 14:42 UTC, after
  a run on it. Its notes: a blue/green warm-up spreads over every green node that joins, not only the first (#4904);
  three gaps in that machinery, found by a randomized soak, are closed (#4909); fleet-scale simulated scenarios, test
  code only (#4905); a chain that only reads through `IQuerySession` but sends gets its commit, in the Marten, Polecat
  and Fisher frame providers (#4910, #4912). The tags also differ by #4903, which the notes leave out: `IgnoreEnvelopes`
  applies inside the `AddStage` stages of a tracked test session. The Kit uses no Marten, Polecat, Fisher, event
  subscription or tracked session. #4904 and #4909 change the leader's agent placement, which every Balanced node runs,
  a single one included, but their new branches act only when the leader moves an agent between nodes; a single node
  only starts its own. Each package stays `MIT` with a `net10.0` asset, and its dependencies change only within the
  `WolverineFx` packages (`WolverineFx.RDBMS` and `WolverineFx.Newtonsoft` follow to 6.48.3): `JasperFx` 2.81.0, Weasel
  9.41.0 and `RabbitMQ.Client` 7.2.2 stay, and restore resolves the same 312 package versions but those seven. No file
  of `Wolverine.RabbitMQ` changed between V6.48.2 and V6.48.3, so the repair is the one described above: #4867 (merge
  commit `cbb1c4e`, 2026-10-07) is an ancestor of V6.48.0, V6.48.2 and V6.48.3 and not of V6.46.0, and the decompiled
  `Wolverine.RabbitMQ.dll` of 6.46.0 and 6.48.2 differ in exactly its two files: `ConnectionMonitor.cs`, which rebuilds
  each agent in its own try/catch and retries a failed rebuild after 1, 2, 5, 10, 20 and 30 s, and
  `RabbitMqChannelAgent.cs`, whose teardown tolerates a closed channel. The drop test above proves recovery after a
  clean drop and delivery after it, and no regression test for #4867 that uses only the broker exists: every refusal a
  single-node broker can produce on demand closes the channel at channel level, and 6.46.0's unchanged channel-shutdown
  hook then rebuilds again, while the exceptions that strand a listener (the client's own disposal errors, a quorum
  queue without a majority) cannot be produced on demand, and upstream's regression test injects an internal fake agent
  the Kit cannot build. The defect was reproduced on neither version. "A Kit Host receives through exactly one durable
  broker listener" holds for durable listeners only (the reply queue's endpoint mode is Inline, not durable in Wolverine's sense, whatever the broker lists; decompiled `RabbitMqQueue.cs:258`, `RabbitMqTransport.cs:557-560`): by Wolverine's default `DeclareRequestReplySystemQueue = true`
  (decompiled `RabbitMqTransport.cs:145`), each Host also listens on an auto-delete `wolverine.response.<guid>` queue,
  a System endpoint, through the same listening connection (decompiled `RabbitMqListener.cs:88`), so "every one after
  it" can apply to a Kit Host. That queue was observed in the broker during a `RabbitMqDeliveryTests` run, one per Host
  with one consumer; the shared connection is read from the decompiled code. A known limit, not a Kit defect: after
  its last retry, about 68 s after the recovery, 6.48.x gives up, and the agent stays without a consumer until the next
  connection recovery, still with no health signal. Proof: `docs/acceptance.md`, the #75 section.

## Amendment (2026-10-10, ticket #82: no EF Core transaction middleware)

The decision above stands: the Unit of Work owns its transaction and writes the Outbox in it.
`UsePouryaKitWolverine(connectionName)` no longer calls `UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager)`,
the line "The registration and commit code that worked" shows, so the decision's "The EF mode is `Eager`" no longer
describes the Kit: it configures no EF Core middleware mode at all.

- **What the line registered.** In WolverineFx 6.48.3 (decompiled `Wolverine.EntityFrameworkCore.dll`,
  `WolverineEntityCoreExtensions.UseEntityFrameworkCoreTransactions(WolverineOptions, TransactionMiddlewareMode)`):
  `IDbContextOutboxFactory`, the scoped `IDbContextOutbox<T>` and `IDbContextOutbox`, `OutgoingDomainEvents`,
  `IDatabaseCleaner<T>`, the EF Core saga store diagnostics and the sources of implicit `DbContext` use; the
  `EntityFrameworkCoreBackedPersistence` extension, which puts EF Core's persistence frame provider first in code
  generation and adds its query and batching pre-compilation policies and `DbContextFactoryRefusalPolicy`; service
  location for every registered `DbContext` in generated code; `Eager` as that frame provider's default mode; and the
  EF Core storage frame provider for ancillary stores.
- **Why the Kit does not need it.** Each of those serves a handler Wolverine generates or a caller of the scoped
  `IDbContextOutbox<T>`, and the Kit has neither. Every Kit handler is pre-built and registered with
  `AddMessageHandler` (ADR 0006), so no generated chain and no EF Core middleware runs in a delivery; the Unit of Work's
  Outbox builds its own `DbContextOutbox` for each commit attempt (the Wave 1 amendment above) and resolves no
  `IDbContextOutbox<T>`. The "EF mode" row of the evidence and the rejected `Lightweight` option concern Wolverine's own
  middleware on a `[Transactional]` handler; for the owned Unit of Work the mode made no difference even in Spike S1.
  An application that adds a Wolverine handler of its own with EF Core middleware calls `UseEntityFrameworkCoreTransactions`
  itself, in the `configure` the call takes.
- **Proof.** With the line and its two `using` directives removed (`4bf4b82`), `dotnet build PouryaKit.slnx` gave 0
  warnings and `dotnet test PouryaKit.slnx` passed 830, 0 failed, the suite's whole count, in 2m27s
  (`docs/acceptance.md`, "Pre-Wave 3 proofs before fixes, ticket #82").

## Amendment (2026-10-10, ticket #96: two different Hosts never share a store's schema)

The decision above stands. "One store per Host", in the spike's registration comment and in "Consequences", and a schema
"configurable per Host", in "Defaults chosen", 3, described the Kit's layout without excluding anything: the Kit's
documentation promised `DurabilityMode.Balanced` safe with any number of instances on one store, and never said that two
different Hosts on one schema are unsupported. Since ticket #96 they are a stated rule, and the upstream guidance that
"Defaults chosen", 3, cites is its reason.

- **The rule.** Instances of one Host share one store and one schema. Two different Hosts, with different handlers,
  Modules or applications, each take a store of their own: another database, or another schema on one database through
  the `schema` argument of `UsePouryaKitWolverine(connectionName, schema, configure)`. Sharing a schema between different
  applications is unsupported.
- **Why.** `JasperFx/wolverine` issue #4923, filed 2026-10-09, reproduced on WolverineFx 6.45.0 and 6.48.2 and open on
  2026-10-10: when two different applications share one envelope-store schema in Balanced mode, the durability agent of a
  node without the handlers recovers the other application's inbox rows and dead-letters them, as a Kit Host does with
  every message it has no handler for (`UsePouryaKitWolverine` sets `UnknownMessageBehavior.DeadLetterQueue`), or drops
  them under Wolverine's default. The maintainer's only comment, of 2026-10-09: "Process A and Process B in your usage
  should *not* be using the same Wolverine storage." Wolverine's PostgreSQL guide says the same in a code comment of its
  sample (`JasperFx/wolverine` tag `V6.48.3`, commit `c1eab2a7f04de7350c3b54741ae00351c6c303d1`,
  `docs/guide/durability/postgresql.md:192-195`): "If separate logical services are targeting the same physical database,
  you should use a separate schema name for each logical application". The Kit's pin, WolverineFx 6.48.3 (the #75
  amendment), was published at 14:42 UTC on 2026-10-09, before the issue was filed at 18:11 UTC, and contains no fix.
- **Documented, not checked.** The owner decided on 2026-10-10 to document the rule and add no runtime guard: Wolverine's
  node record holds no application identity (its insert writes an id, the control URI, capabilities, a description and a
  version: `JasperFx/wolverine` tag `V6.48.3`, `src/Persistence/Wolverine.Postgresql/PostgresqlNodePersistence.cs:66`),
  so a check would have nothing to observe. `MessageStoreDatabaseCheck` (the #63 amendment) compares one Host's
  DbContexts with that Host's own store and cannot see another Host. The Proving Sample keeps the rule with a database for
  each of `ModularMonolith.Host` and `Inventory.Host`, and `BrokerFixture` gives each of its two Hosts a database of its
  own.
- **What the tests cover.** `SharedStoreSchemaTests` proves one Host whose store shares its Module's schema: a reset clears
  the Module's rows and the store still delivers. `MessageStoreDatabaseTests` proves the check of one Host's DbContexts
  against that Host's own store. No test covers two applications on one store.
- **Where the rule is stated.** The `Durability` summary of `PouryaKitMessageStoreOptions`, the Kit's skill
  (`messaging.md`, "The message store") and `docs/architecture/README.md` (`PouryaKit.Messaging.Wolverine.EFCore`). The
  text above keeps its wording. The skill's `rabbitmq.md` keeps its own rule for the broker: two different Hosts never
  share a service name, and so never a queue.

## Amendment (2026-10-10, ticket #87: the generated test project through `PouryaKit.Testing`)

Two lines of the ticket #58 amendment no longer describe the templates. Each template Host composes in
`AppHost.CreateBuilder(args)` and `AppHost.Build(builder)`, as the Proving Sample's `ModularMonolithHost` does, and its
`Program.cs` is `return await AppHost.Build(AppHost.CreateBuilder(args)).RunJasperFxCommands(args);`: every Host still
owns the command line. A generated solution's test project references `PouryaKit.Testing`, which brings
`WolverineFx.RuntimeCompilation`, instead of the package itself, and hosts the Host through `AppHost` with
`HostFixture`, never through `Program`, so it sets nothing for JasperFx. The Testing package's exception stands as
recorded: no other Kit package references the compiler.

## Amendment (2026-10-16, ticket #120: a Stream commits through Wolverine's Marten outbox)

The decision above stands for the EF write model. Each write model has one commit owner; the EF Unit of Work owns rows
and commits through the EF outbox; the Marten Unit of Work owns a Stream and commits through Wolverine's Marten outbox;
no Operation commits through both. The Marten Unit of Work keeps this record's shape with Wolverine's Marten outbox: it
owns a connection and a transaction from before the command, as this record's Unit of Work owns its DbContext's, opens
its session over them, writes the envelopes through a new `MartenOutbox` enrolled in the session for each commit
attempt, as the EF Unit of Work builds a new `DbContextOutbox`, and saves, which commits and then flushes in the listener
`MartenOutbox` adds to the session. When the session holds no work, as when a Receipt or an idempotency key is the
Operation's only write, Marten's save commits nothing, and the Unit of Work commits the transaction itself. Marten's Wolverine integration registers a second main
message store, which Wolverine keeps as this one store, with one durability agent, only when it names this store's
database and schema; otherwise the Host fails when it starts. A Notification committed by a process killed between
Marten's commit and the flush is delivered after a restart, as recorded above for the EF outbox. The evidence and the
invariants for #121, #131 and #132 are in [ADR 0011](0011-each-write-model-has-one-commit-owner.md).

## Amendment (2026-10-24, ticket #167: WolverineFx 6.52.0)

The decision above stands. The seven `WolverineFx*` pins move from 6.48.3 to 6.52.0, the latest stable on nuget.org on
2026-10-24, and `Marten` from 9.46.0 to 9.53.0 (spec #161, owner decision W8). "Pinned versions and licences" stays as
the record of what Spike S1 ran on, and each amendment above keeps the version it was read on.

- **What restore moved.** `JasperFx`, `JasperFx.Events` and `JasperFx.SourceGenerator` from 2.81.0 to 2.91.0 (2.94.0
  where Marten is restored), every `Weasel.*` package from 9.41.0 to 9.46.0, and `WolverineFx.RDBMS` and
  `WolverineFx.Newtonsoft` with the pins, all `MIT`; one package is new, `System.Linq.Dynamic.Core` 1.7.4,
  `Apache-2.0`, through `JasperFx`, and none left. `LicenceTests` passes.
- **The release notes, 6.49.0 to 6.52.0** (`JasperFx/wolverine` releases). 6.52.0 states no breaking API change. Its two
  behaviour changes miss the Kit: recurring schedules under 15 minutes run on a timer agent, and the Kit's recurring
  commands are its own chain of scheduled envelopes, not Wolverine schedules; envelopes a transport cannot map are
  dead-lettered on Azure Service Bus, SNS and Kafka, and the Kit's broker is RabbitMQ. It marks
  `MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler` obsolete, which the Kit never names. 6.50.0's
  Wolverine.HTTP body binding and 6.49.0's SQL Server envelope ids do not reach a Kit Host.
- **The inbox stale time (6.51.0, `JasperFx/wolverine` GH-5022).** The finding above that nothing updates the
  `timestamp` the release compares no longer holds. At tag `V6.52.0` the release takes only rows with status `Incoming`
  (`src/Persistence/Wolverine.RDBMS/Durability/BumpStaleIncomingEnvelopesOperation.cs`), the scheduled-job poll's claim
  sets `timestamp` to the database's `now()` (`src/Persistence/Wolverine.RDBMS/MessageDatabase.cs`,
  `restartIncomingStaleClock`, used by `src/Persistence/Wolverine.Postgresql/PostgresqlMessageStore.cs`), and a durable
  receiver restarts the clock of every envelope it holds, queued or running, three times per stale time
  (`src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs`, `InboxCustody.cs`). So a due retry, delayed command or
  recurring occurrence is not stale when it starts (`InboxStaleTimeTests`), and the #17 amendment's rule, that the
  stale time covers the retry schedule, is retired (ADR 0006's amendment for #167). The release compares the Host's
  clock, less the stale time, with the database's, and a held envelope is released when its receiver misses a restart:
  in the Kit's loaded test run a 2-second stale time let a held retry be handed out twice. The stale time therefore
  stays well above the clock difference and a loaded Host's delay, as the 5-minute default does. The remedy for a
  Notification committed and never sent is unchanged: no receiver holds it, and it is released after the stale time
  (`NotificationTests`).
- **The namespace rule.** `JasperFx.SourceGenerator` 2.91.0 writes an internal `JasperFx.Generated.DiscoveredEventModels`
  into every assembly that sees `JasperFx.Events`, marked only by an auto-generated comment; `NamespaceTests` leaves it
  out by its full name.
- **Proof.** The build has no warning, both test projects pass, and `validate.py` passes on the bumped tree
  ([acceptance](../acceptance.md#wave-4-dependency-refresh-ticket-167)).
