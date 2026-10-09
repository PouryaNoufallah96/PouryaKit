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
