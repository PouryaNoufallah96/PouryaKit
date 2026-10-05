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

## Evidence

Spike code: `spikes/s1-atomic-commit/` at commit `64e2b165ee7444b9c615812bcce25ae2d377115a`. It is not
merged. Run on 2026-10-05 with `dotnet test spikes/s1-atomic-commit` (.NET SDK 10.0.401, Microsoft Testing
Platform), against `postgres:18.6` in Testcontainers 4.15.0. 33 of 33 tests passed. Each test gets a fresh
database. Catalog (`catalog` schema) and Inventory (`inventory` schema) each have their own DbContext, and
each Module publishes one Notification to a durable local queue. The handler records every delivery in
`spike.deliveries`. No EF retrying execution strategy is configured anywhere.

| Point | Deciding test (class.method, all cases) | Observed |
|---|---|---|
| Atomic commit, each Module | `AtomicCommitTests.Commit_writes_business_row_and_envelope_row_in_one_transaction` (Catalog and Inventory × Raw and mapped × Eager and Lightweight) | After `SaveChangesAsync` and before commit, the Operation's own connection sees the business row and the envelope row, and a second connection sees neither. After commit both rows exist with the same `pg_xact_commit_timestamp`, which means one commit. The message is delivered only after the flush. |
| Rollback after save | `AtomicCommitTests.Exception_after_save_rolls_back_business_row_and_envelope_row` | No business row, no envelope row, and no delivery within 3 s. |
| Failed Result | `AtomicCommitTests.Failed_outcome_after_publish_leaves_no_business_row_and_no_envelope_row` | The same, although publish had already written the envelope row inside the transaction. |
| Failed save | `AtomicCommitTests.Failed_save_leaves_no_envelope_row` | A duplicate key in `SaveChangesAsync` leaves no envelope row and no delivery. |
| Crash after commit | `CrashRecoveryTests.Message_committed_before_the_process_is_killed_is_delivered_after_restart` | A child process (`dotnet S1.Host.dll`) commits, prints `COMMITTED` before flushing, and is killed with `Process.Kill` (SIGKILL). The envelope row stays `Incoming`, owned by the dead node, with no delivery. A restarted Host delivers it exactly once: in 4.9 s in Balanced mode with shortened timers, in **74.6 s in Balanced mode with Wolverine's default timers**, and in 3.9 s in Solo mode. |
| Crash during handling | `CrashRecoveryTests.Message_whose_handler_was_running_when_the_process_was_killed_is_handled_again_after_restart` | The handler's side effect was already written when the process was killed. After restart the handler runs again, so delivery is at-least-once. |
| EF mode | `EfModeTests.Middleware_handler_effect_and_inbox_mark_share_a_transaction_only_in_eager_mode` (Eager and Lightweight × Raw and mapped) | For a `[Transactional]` Wolverine handler, `Eager` opens a transaction before the handler runs, and the effect and the inbox `Handled` mark commit together. `Lightweight` opens no transaction, so the effect commits alone and the mark commits about 1 ms later in a separate transaction. For the owned Unit of Work the mode makes no difference: every case of the first row passes in both modes. |
| One store, two DbContexts | `ObservationTests.One_message_store_in_the_wolverine_schema_serves_both_module_DbContexts` | See "Observed behaviour" below. |
| Send without the outbox | `ObservationTests.Publish_through_the_plain_message_bus_escapes_the_unit_of_work_and_is_delivered_after_rollback` | See below. |
| Flush after rollback | `ObservationTests.Flushing_the_outbox_after_a_rollback_delivers_a_message_whose_business_row_was_rolled_back` | See below. |
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
so no `stop_if` applies. The closure includes two heavy transitive packages:

- `Microsoft.EntityFrameworkCore.Design` 10.0.4, through WolverineFx.EntityFrameworkCore. Plan section C
  already expects it.
- Roslyn (`Microsoft.CodeAnalysis.*` 5.0.0), through WolverineFx.RuntimeCompilation.

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

- **Send timing with the outbox.** Publishing writes the envelope row at once, inside the Unit of Work's
  transaction. With a Wolverine-mapped DbContext it is written at `SaveChangesAsync` instead. Nothing is sent
  until `FlushOutgoingMessagesAsync`.
- **Send timing without an enrolled transaction.** `IMessageBus.PublishAsync` stores the envelope on its own
  connection and sends it at once. The handler had already run while the Operation's transaction was still
  open, and the rollback that followed could not undo it.
- **A flush after a rollback still sends.** Rollback removes the envelope row, but the outbox keeps the
  envelope in memory. A flush after rollback delivered a Notification whose business row does not exist. The
  Unit of Work must never flush after a rollback.
- **Queue naming defaults.** There is one durable local queue per message type, named after the type's full
  name in lower case: `local://s1.productcreated/` and `local://s1.stockitemcreated/`.
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
- **Recovery.** A node killed after commit leaves rows owned by its node number. In Balanced mode they are
  released only after the node is judged stale and the orphan sweep runs
  (`src/Persistence/Wolverine.RDBMS/Durability/ReleaseOrphanedMessagesCommand.cs`). That took 74.6 s with
  defaults. In Solo mode the restarted node takes the same number, 1, and delivered them 3.9 s after it
  started.

## Invariants for permanent tests

| Invariant | Seam | Owner |
|---|---|---|
| A failed Result, a failure after save, or a failed save leaves no business row, no Outbox row and no delivery, for each Module | Gateway | #15 |
| A successful Operation's business rows and Notifications become visible together, and the Notification is delivered | Gateway | #15 |
| A Notification committed before the Host dies is delivered after restart (the test needs a stop point between commit and flush) | Gateway | #15 |
| Feature code never reaches `IMessageBus`, `IMessageContext` or `IDbContextOutbox<>`; only the Unit of Work publishes and flushes | Structure | #15 |
| Module DbContexts do not map Wolverine envelope storage (`MapWolverineEnvelopeStorage`, `AddDbContextWithWolverineIntegration`) | Structure | #15 |
| A message re-handled after a crash during handling applies its effect once, through the handler's Receipt | Gateway | #16 |

## Notes for S2

- Wolverine's inbox mark is per envelope, not per handler. Only `Eager` middleware commits it with a
  handler's effect. A crash between effect and mark re-runs the handler, as the second crash test showed, so
  independent retry per handler still needs the Kit's Receipt (G.2).
- `DurabilitySettings.MessageIdentity` defaults to `IdOnly`. Its source comment recommends `IdAndDestination`
  for modular monoliths that receive one message on several endpoints, which bears on separated per-handler
  queues. This was not tested.
- Each message type had one handler here. What one local queue does with several handlers of the same type
  was not tested.
- Retry and recovery tests need short durability timers or Solo mode. The defaults take more than a minute
  to recover a dead node's envelopes.
- Woo's block also sets `ConventionalLocalRoutingIsAdditive`, `UseDurableInboxOnAllListeners`,
  `UseDurableOutboxOnAllSendingEndpoints`, `RetryWithCooldown` and the inbox and outbox stale times. S1 used
  none of them.

## Considered Options

- **`IDbContextOutbox<T>.SaveChangesAndFlushMessagesAsync`.** Rejected. It saves, commits whatever transaction
  is open and flushes in one call, so the commit and the "no flush after rollback" rule leave the Unit of
  Work (ADR 0003).
- **Wolverine-mapped DbContexts** (`MapWolverineEnvelopeStorage("wolverine")`). This passes every test, but
  rejected. It puts Wolverine entities into every Module's model, makes each DbContext repeat the store
  schema (a wrong one fails the commit), and is a second way to do the same thing.
- **`Lightweight` mode.** It gives the same result for the owned Unit of Work, but rejected. For any handler
  that uses Wolverine's EF middleware, it commits the effect and the inbox mark separately.
- **Solo durability mode.** It recovers fastest, but it assumes a single node. The Spike kept Balanced, which
  is Wolverine's default.

## Consequences

- The Unit of Work always holds an explicit transaction. That is incompatible with an EF retrying execution
  strategy, which matches the decision to run none. A whole-Operation retry starts a new scope (ADR 0003).
- The Host must reference `WolverineFx.RuntimeCompilation`, or pre-generate handler code
  (`TypeLoadMode.Static`). The package graph and Testing must account for Roslyn in the closure.
- Physically, the Outbox is one Wolverine store per Host. Each Module's Notifications are distinguished by
  message type and local queue.
- Disposing a Host logs `ObjectDisposedException` errors from Wolverine's database batcher. They are noise,
  and test log assertions should tolerate them.
