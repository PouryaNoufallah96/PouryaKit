---
status: accepted
---

# Each write model has one commit owner; a Stream commits through Wolverine's Marten outbox in the Kit's Marten Unit of Work

Spike #120 (spec #115, "Stream write model") ran a Stream write model beside the EF one against Marten 9.46.0,
WolverineFx.Marten 6.48.3 and PostgreSQL, through the Kit's own dispatcher, Modules, delivery step and message store.
**Each write model has one commit owner. The EF Unit of Work owns rows and commits through the EF outbox; the Marten
Unit of Work owns a Stream and commits through Wolverine's Marten outbox; no Operation commits through both.** A Module
declares one write model, so the Unit of Work its Composition Root registers, which `AddPouryaKitModules` keys by the
Module, owns every commit of every Operation in that Module; a Host may hold both write models on its one message
store. The Marten Unit of Work is the EF one's shape (ADR 0004): it owns a connection and a transaction, opens its
session over them, runs the command, drains the Domain Events, appends each Stream's events with `FetchForWriting` at
the version it read, writes the envelopes through a new `MartenOutbox` enrolled in the session, and saves, which commits
and then flushes in Wolverine's session listener; when the session holds no work, as when a Receipt or an idempotency
key is the Operation's only write, Marten's save commits nothing, and the Unit of Work commits its transaction itself.
Nothing observed contradicts ADR 0001 to 0010 or section G. Plan I.4's internal `Apply`
methods do not hold on these versions, and a Kit typed ID is no Marten identity ("Findings" below); neither touches the
decision.

## Evidence

The Spike code is `spikes/marten-two-store/` at commit `d03cc7409c9cc5e9c10520142553d473d763b68d`, reachable through tag `spike/marten-two-store`.
It is not merged. It was run on 2026-10-16 with `dotnet test spikes/marten-two-store` (.NET SDK 10.0.402, Microsoft
Testing Platform, xunit.v3 4.0.1, `postgres:18.6` with `track_commit_timestamp=on` and `max_connections=300` in
Testcontainers 4.15.0, Docker 29.8.2, macOS arm64), on the Kit at `10df372f`: 34 of 34 tests passed in about 25 s. Each
test gets a database of its own; tests run one at a time.

**Configuration.**

- **The Kit, unchanged, by `ProjectReference`.** `AddPouryaKitModules` (each Module's `IUnitOfWork` keyed by the Module,
  and the routed `IReadingStateGuard`), the dispatcher, `UsePouryaKitWolverine("app")` of
  `PouryaKit.Messaging.Wolverine.EFCore` (the message store in schema `wolverine`, one durable local queue per
  subscription, the delivery step `Deliver` with its `IReceipts`) and `UsePouryaKitTestDelivery` (Solo durability, 100 ms
  polling, retries after 100 ms and 100 ms). Nothing under `src/` or `samples/` changed.
- **`M2.Marten`, what #121 and #131 would ship, as a self-contained copy**: `MartenUnitOfWork` (`IUnitOfWork`,
  `IReadingStateGuard`, and `IStreams`, the Feature code's way to load and start a Stream), `StoreNeutralOutbox` (the
  part of `WolverineOutbox.WriteAsync` that names no store), `MartenReceipts`, `MartenIdempotencyStore`,
  `MartenIdempotencyCleanup`, `MartenStoreDatabaseCheck`, the per-Module resolvers, and `DocumentReceipts` (a rejected
  option).
- **One Marten store per Host**: `AddMarten` over the connection string `app`, events and documents in schema `events`,
  `UseSystemTextJsonForSerialization(configure: json => json.UsePouryaKitJson())`, `Events.TenancyStyle = Conjoined`,
  `Policies.AllDocumentsAreMultiTenanted()`, `ApplyAllDatabaseChangesOnStartup()`,
  `IntegrateWithWolverine(integration => integration.MessageStorageSchemaName = "wolverine")`. Marten 9's default
  append mode, Quick, is kept.
- **`M2.Ledger`, a Stream Module**: the Stream `Account` (an internal class, its `Apply` methods public), commands that
  start and change it, the Notification Handler `ledger.welcome` of `AccountOpened` (it appends `Welcomed` and publishes
  the Integration Event `AccountOpenedV1`), `ledger.note-deposit` of `Deposited`, which writes nothing, and the
  `[Idempotent]` commands `OpenAccountCommand` and `PingCommand`, which changes no Stream. Its Receipts, Snapshot versions and idempotency keys are tables of schema `ledger`, of the EF
  tables' shapes, which Marten creates as extended schema objects of the store.
- **`M2.Catalog`, an EF Module**: `UsePouryaKitPostgres<CatalogDbContext>("app", "catalog")` with no entities, the
  subscription `catalog.account-opened` of `AccountOpenedV1`, and an `[Idempotent]` command.
- **Between commit and flush**: a Marten store listener (`StoreOptions.Listeners`) runs `AfterCommitAsync` before the
  `FlushOutgoingMessagesOnCommit` listener each `MartenOutbox` adds to its session, so a test reads the store there.
- **Red once.** Each deciding test was run once with its behaviour switched off and failed; the switch is named in its
  row. Those runs were on the Kit at `10df372f`, on the code of the review's fix round.

## The items

The ticket and the spec count fifteen, nine from plan I.4 and six "from the code", but the spec's list of code items has
seven clauses; all sixteen are recorded. Every outcome is observed in the test it names unless the row says inferred.

| # | Item | Outcome | Test (`M2.Tests`), or source | Red once with |
|---|---|---|---|---|
| 1 | One message store, one durability agent | Pass. Two `IMessageStore` registrations, one store in `runtime.Stores.FindAllAsync()`, one durability agent in `AllKnownAgentsAsync()`, the main store's schema `wolverine`. The trap: Marten's envelopes in schema `marten_wolverine` fail the start with `InvalidWolverineStorageConfigurationException`, "There must be exactly one message store tagged as the 'main' store". Wolverine keeps one store per Uri, `wolverinedb://postgresql/<host>/<database>/<schema>` (read: `JasperFx/wolverine` tag `V6.48.3`, `src/Wolverine/Persistence/MessageStoreCollection.cs:110-122,149-167`) | `MessageStoreTests.One_message_store_and_one_durability_agent_serve_both_write_models`; `MessageStoreTests.A_marten_integration_with_its_envelopes_in_another_schema_fails_the_host_at_start` | Marten's schema `marten_wolverine`: the first test failed at start |
| 2 | An envelope commits with its `mt_events` row and is delivered through the Kit's routed queue | Pass. Between commit and flush, the event row and the envelope row have one `pg_xact_commit_timestamp`; the envelope is `Incoming`, owned by the node, received at `local://ledger.welcome/`, the Kit's queue of that subscription; the Kit's delivery step then runs the Notification Handler | `CommitTests.An_envelope_commits_in_the_transaction_of_its_events_and_is_delivered_through_the_kits_routed_queue` | Envelopes written through a `MessageContext` not enrolled in the session |
| 3 | A version mismatch is `Conflict` with no envelope | Pass, in both windows: the stream moved after the handler read it (`FetchForWriting` throws `JasperFx.ConcurrencyException`, "Expected the existing version to be 2, but was 3"), or while the first Operation saved (`mt_quick_append_events` raises MT003, surfaced as `JasperFx.Events.EventStreamUnexpectedMaxEventIdException`). Either is `UnitOfWorkErrors.ConcurrencyConflict`, one `Deposited` event and one envelope | `CommitTests.A_stream_that_moved_after_it_was_read_is_the_kits_conflict_and_sends_nothing` (both cases). The exception types were read in a diagnostic run that logged the caught type | `FetchForWriting` without the version read |
| 4 | A duplicate stream start appends nothing and sends nothing | Pass, in both windows: after the first committed (`FetchForWriting(id, 0)` throws `ConcurrencyException`, "expected 0, but was 1"), or while the first saved (`Marten.Exceptions.ExistingStreamIdCollisionException` at the save). One `AccountOpened`, one envelope | `CommitTests.A_stream_started_twice_appends_nothing_and_sends_nothing_the_second_time` (both cases) | as item 3 |
| 5 | Typed IDs serialise in Marten as the Kit's JSON writes them | Pass for data: the event's `customer` member is `JsonSerializer.Serialize(customerId, new JsonSerializerOptions().UsePouryaKitJson())`, the bare Guid, and folds back. A Kit typed ID is no Marten identity: a document keyed by one fails its save with `InvalidDocumentException`, "No closed-shape id strategy is registered". Marten takes only immutable structs as strong-typed identities, and none for a stream (read: `JasperFx/marten` tag `V9.46.0`, `docs/documents/identity.md:292-302,335`) | `TypedIdTests.A_typed_id_in_an_event_is_stored_as_the_kits_json_writes_it_and_folds_back`; `TypedIdTests.A_kit_typed_id_is_not_taken_as_a_marten_document_identity` | Marten's own JSON settings |
| 6 | A crash between commit and flush recovers as ADR 0004 records | Pass. A child process (`dotnet M2.Host.dll crash ...`) prints `COMMITTED` from the store listener and is killed (`Process.Kill`, SIGKILL): `AccountOpened` committed, its envelope `Incoming` and owned by the dead node, nothing welcomed. A restarted Host, Solo, delivers it with its Receipt; the whole test took about 2 s. Balanced recovery was not re-timed; it is the same store and durability agent as ADR 0004's 74.6 s with default timers (inferred) | `CrashRecoveryTests.A_notification_committed_before_the_process_is_killed_is_delivered_after_restart` | No outbox write: no envelope to recover |
| 7 | A Ledger Notification Handler's Integration Event and Receipt commit with its Stream effect | Pass. The `Welcomed` event, the Receipt `ledger.welcome`, the publication record `ledger.account-opened.v1#published` and the `AccountOpenedV1` envelope to `local://catalog.account-opened/` have one commit timestamp; Catalog's EF delivery then records its Receipt | `CommitTests.A_notification_handlers_integration_event_and_receipt_commit_with_its_stream_effect` | A session not opened over the Unit of Work's transaction, so the Receipt statements run alone and Marten commits its own at the save |
| 8 | A concurrent duplicate delivery is applied once | Pass. With an inbox stale time of 500 ms and the handler holding 2 s, the store hands the delivery out again; the duplicate's Receipt insert waits on the first one's row lock (seen in `pg_stat_activity`), then inserts nothing, so its handler never runs: one run, one `Welcomed`, one Receipt. A handler whose Receipt is its delivery's only write, `ledger.note-deposit`, keeps it, and a redelivery never runs the handler again | `ReceiptTests.A_concurrent_duplicate_delivery_waits_for_the_first_receipt_and_never_runs_its_handler`; `CommitWithoutWorkTests.A_notification_handler_that_writes_nothing_keeps_its_receipt_and_a_redelivery_never_runs_it` | A Receipt statement that records nothing; for the second test, no commit of the Unit of Work's own when Marten has no work |
| 9 | A failed handler rolls its Receipt back with the Stream | Pass. Each of the three attempts ran the handler, so no failed attempt's Receipt survived; then a dead letter, and no `Welcomed`, no Receipt, no Integration Event | `ReceiptTests.A_failed_notification_handler_rolls_its_receipt_back_with_its_stream_effect_and_its_integration_event` | as item 7 |
| 10 | The Marten Unit of Work's own write seam, with the store-neutral Outbox part shared | Pass as a mechanism. One copy of the store-neutral write (the route filter, the Operation Context headers and tracestate, the Integration Event's identity header and its publication record) writes through `DbContextOutbox` and through `MartenOutbox`, both a `MessageContext`; each stores an envelope to the same queue, of the same message type, with equal Operation Context headers. The Marten Unit of Work takes the Integration Events from the Kit's own scope buffer, `PublishedIntegrationEvents`, internal to `PouryaKit.Messaging.Wolverine.EFCore`, reached here by reflection. Moving that part into `PouryaKit.Messaging.Wolverine` changes `src/`, so it is #121's invariant below | `StoreNeutralOutboxTests.One_write_stores_the_same_routed_envelope_through_the_ef_and_the_marten_outbox` | No Operation Context headers |
| 11 | Receipts and the idempotency store selected per Module | Pass as a mechanism. Ledger's keyed `MartenReceipts` and `MartenIdempotencyStore`, Catalog's keyed `EfReceipts` and `EfIdempotencyStore`, and unkeyed resolvers that read `Operation.Module`: Ledger's Receipts and keys land in `ledger`, Catalog's in `catalog`, and an idempotent replay replays, with the resolvers registered before the Kit's registrations, after them, or twice; an idempotent Ledger command that changes no Stream keeps its key and replays its success. The Kit's EF types are internal and keyed here by name; their packages keying them, and `AddPouryaKitModules` adding the resolvers, change `src/`, so they are #121's invariant | `PerModuleStoreTests.Each_module_keeps_its_receipts_and_idempotency_keys_in_its_own_store_whatever_the_registration_order` (three cases); `CommitWithoutWorkTests.An_idempotent_command_that_changes_no_stream_keeps_its_key_and_replays_its_success` | No resolvers: the Kit's `EfReceipts` serve Ledger and throw; for the second test, as item 8's |
| 12 | The reading-state guard over Marten's pending changes | Pass. The Marten Unit of Work is the scope's `IReadingStateGuard` and compares, across a step that only reads, the Streams started, the events raised on the Streams it tracks, the identity of each tracked Stream, and Marten's `PendingChanges` operations and streams. A query sent from a command that deposits is refused, the command that swallows the refusal fails, nothing commits. A Stream's `Id` is settable, since Marten's fold sets it, so a query that points a tracked Stream at another stream of the same version while its events are pending is refused too; and the append goes to the identity captured when the Unit of Work began tracking the Stream, whatever its `Id` says later. Any other in-place change that raises no event is never appended, so it never persists (inferred from the design) | `ReadingStateTests.A_query_that_changes_a_stream_marks_its_operation_rollback_only_and_nothing_commits`; `ReadingStateTests.A_query_that_points_a_tracked_stream_at_another_while_its_events_are_pending_is_refused_and_nothing_commits` | The comparison off: the deposit committed; the identity left out of the comparison: the second test failed |
| 13 | The structure rules extended to Marten, Weasel and the document session | Pass as a mechanism, with a finding. The Kit's rule as written (`ModuleRules.TransportTypesUsedBy`: no type from an assembly named `Wolverine*` or `JasperFx*`) rejects every Stream Module, for the evolver Marten's source generator writes into it (`IGeneratedSyncEvolver<,>` and three more `JasperFx.Events` types), and sees an offending use of `IDocumentSession` only through the marker attribute the generator puts on every assembly it runs over. Extended to `Marten*` and `Weasel*`, with those four generated types allowed, Ledger passes and the offending assembly is named for `Marten.IDocumentSession` and `Marten.IDocumentOperations` | `StructureTests.Feature_code_of_a_stream_module_names_no_marten_weasel_wolverine_or_jasperfx_type_but_the_generated_evolvers` | The rule without `Marten*` |
| 14 | The Marten store's connection in the message-store database check | Pass as a mechanism. A composition check that reads the database of the data source Marten built, without connecting, fails the build when it is not the message store's; without it the two main stores fail only at start. Wolverine's Marten outbox writes each envelope on the session's connection into the main store's tables (read: `V6.48.3`, `src/Persistence/Wolverine.Marten/MartenEnvelopeTransaction.cs:16-59`), so a store on another database without the integration would write envelopes there (inferred) | `StoreDatabaseTests.A_marten_store_on_another_database_than_the_message_store_fails_the_host_when_it_is_built`; `StoreDatabaseTests.Without_the_check_the_two_databases_fail_the_host_only_at_start` | The check off |
| 15 | Idempotency keys of a Marten Unit of Work expire under the same retention options | Pass, in a Marten-only Host and a mixed one. A copy of `IdempotencyKeyCleanup` over the Stream Modules' tables, under `PouryaKitIdempotencyOptions`: a key reserved ten years ago is removed and a new one kept (one hour retention, 100 ms interval), in both stores of the mixed Host; `Timeout.InfiniteTimeSpan` keeps every key | `IdempotencyRetentionTests.A_key_reserved_longer_than_the_retention_ago_is_removed_and_a_newer_one_kept`; `IdempotencyRetentionTests.An_infinite_retention_keeps_every_key` (each in both Hosts) | No Marten cleanup |
| 16 | Conjoined tenancy with the Kit's tenant | Pass. The session opens for `OperationContext.Tenant`; the delivery restores it from the envelope's headers, so the welcome is appended under it too; another tenant and no tenant read nothing. One stream id is a stream of its own under each tenant, and an Operation with no tenant writes under Marten's default tenant `*DEFAULT*` | `TenancyTests.A_stream_is_kept_and_delivered_under_the_operations_tenant_and_no_other_tenant_reads_it`; `TenancyTests.One_stream_id_is_a_stream_of_its_own_under_each_tenant_and_no_tenant_is_martens_default` | A session opened without the tenant Not checked by the Spike: an explicit `*DEFAULT*` tenant, Marten's reserved default, passes `OperationContext` and would alias the tenantless streams; #131's invariant below refuses it |

Read in source for the commit shape (`JasperFx/wolverine` tag `V6.48.3`, commit `c1eab2a7f04de7350c3b54741ae00351c6c303d1`;
`JasperFx/marten` tag `V9.46.0`, commit `b2f530ecfd38c454a609989f2d75ae39e3553d41`):

- `IntegrateWithWolverine` registers `IMartenOutbox` scoped and a second `IMessageStore` singleton over Marten's own data
  source, in `MessageStorageSchemaName`, else the Durability schema, else Marten's schema
  (`src/Persistence/Wolverine.Marten/WolverineOptionsMartenExtensions.cs:71-77,121-136,335-353`).
- `new MartenOutbox(runtime, session)` enrolls the session and adds `FlushOutgoingMessagesOnCommit`, whose
  `AfterCommitAsync` returns the flush (`src/Persistence/Wolverine.Marten/IMartenOutbox.cs:23-37`,
  `FlushOutgoingMessagesOnCommit.cs:140-150`). Marten runs the session's listeners in order after the commit
  (`src/Marten/Internal/Sessions/DocumentSessionBase.SaveChanges.cs:87-90`), so `SaveChangesAsync` commits, then flushes,
  and an exception from the flush leaves `SaveChangesAsync` after the commit (inferred; not tested).
- `SaveChangesAsync` returns at once, committing nothing, when the session holds no operation, stream or event operation
  (`DocumentSessionBase.SaveChanges.cs:21-33`, `src/Marten/Internal/UnitOfWork.cs:227-230`). A Receipt or an idempotency
  key written with `AdvancedSql` is a statement already run, not queued work. Over a transaction the caller gave with
  `SessionOptions.ForTransaction(transaction, shouldAutoCommit: true)`, Marten commits it after a batch and leaves it
  alone on dispose (`src/Marten/Internal/Sessions/ExternalTransaction.cs:46-54,96-103,296-307`).
- `Append(streamId, expectedVersion, ...)` needs `EventAppendMode.Rich`; `FetchForWriting` works in Quick mode
  (`docs/events/appending.md:210-230`).

## Rejected options, one honest attempt each

- **Two stores.** With Wolverine's opt-in `Durability.ResolveMainStoreOnConflict` choosing the Kit's store, the Host
  starts with two stores and two durability agents, and the Marten outbox still writes into the main store's tables
  (`MartenEnvelopeTransaction` takes the runtime's main store), so the second agent polls tables nothing writes:
  `MessageStoreTests.Two_stores_reconciled_into_one_main_run_two_durability_agents_and_the_marten_outbox_writes_to_the_main`.
  It works and gains nothing: a second set of Wolverine tables and a second agent. Rejected.
- **Receipts as Marten documents** on the same session. The effect is applied once, but a document is written only at the
  save, so a concurrent duplicate finds no Receipt, runs its handler, and is refused at the save; its retry applies
  nothing: `ReceiptTests.With_document_receipts_a_concurrent_duplicate_runs_its_handler_and_is_refused_at_the_save`.
  `IReceipts` promises that a concurrent delivery waits for the first one, and a Notification Handler that reaches outside
  its data (G.2) would act twice. Rejected for the EF tables' statements on the session's transaction.
- **Wolverine's aggregate-handler workflow as the commit owner.** In a plain Wolverine Host it appends through
  Wolverine's own generated chain, run by `IMessageBus`, so the Kit's dispatcher, pipeline and Unit of Work never run it,
  and Wolverine refuses an internal handler type, "Handler types must be public, concrete, and closed", which a Module's
  internal Features (ADR 0001) are:
  `AggregateHandlerWorkflowTests.The_aggregate_handler_workflow_commits_through_wolverines_own_chain_and_refuses_an_internal_handler`.
  Rejected, as plan I.4 read from the docs.

## Findings

- **`Apply`, `Create` and `ShouldDelete` must be public.** Marten 9 folds through a dispatcher the
  `JasperFx.Events.SourceGenerator` analyzer, shipped in the Marten package, writes at compile time; it takes only public
  conventional methods, and an internal, but not private or protected, type. With internal `Apply` methods,
  `FetchForWriting` throws `InvalidProjectionException`, "No source-generated dispatcher found"
  (`AggregateConventionTests.A_stream_with_internal_apply_methods_has_no_generated_dispatcher`; the rule read decompiled
  from `analyzers/dotnet/cs/JasperFx.Events.SourceGenerator.dll` of Marten 9.46.0). Plan I.4's "may be internal" does not
  hold; ADR 0001 still holds, since the type is internal.
- **A Stream's identity is a Guid** (item 5). G.3 still holds: a typed ID wraps that Guid in Feature code and in the
  Kit's Stream API, as EF Core stores a typed ID as its value, and Marten sees only the value. The Spike's `IStreams` took
  the bare Guid; the typed-ID surface of the Stream API is #131's to settle.
- **The generator writes into the Module's assembly** (item 13), so the structure rule allows its output by name.
- **The Unit of Work owns the transaction, from before the command to its commit.** Opened over it, the session runs the
  Receipt and the idempotency key in it; opened without it, they commit on their own: the red runs of items 7 and 9.
  Marten commits it when it saves work; when the session holds none, the Unit of Work commits it, or the Receipt of a
  handler that writes nothing else, and the key of an idempotent command that changes no Stream, would be rolled back
  (review finding F1; items 8 and 11).
- **A Stream's identity is captured when tracking begins** (review finding F4; item 12). Its `Id` stays settable for
  Marten's fold, so the Unit of Work appends to the identity it captured and refuses a step that only reads and changes
  it.
- **A Marten-only Host still needs the EF package's registration.** `PersistMessagesWithPostgresql` is reached only
  through `UsePouryaKitWolverine(connectionName)` of `PouryaKit.Messaging.Wolverine.EFCore`, and the validation of
  `PouryaKitIdempotencyOptions` is registered only by `AddPouryaKitEfCore`; the Marten-only Host of item 15 ran with the
  first and without the second.

## The registration and commit code that worked

Host, abridged from `M2.Marten/MartenRegistration.cs` and `M2.Host/SpikeHost.cs`:

```csharp
builder.AddPouryaKitModules(modules => { modules.Add<CatalogModule>(); modules.Add<LedgerModule>(); });
builder.Services.AddMarten(options =>
    {
        options.Connection(connectionString);                      // the message store's database
        options.DatabaseSchemaName = "events";
        options.UseSystemTextJsonForSerialization(configure: json => json.UsePouryaKitJson());
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Policies.AllDocumentsAreMultiTenanted();
    })
    .ApplyAllDatabaseChangesOnStartup()
    .IntegrateWithWolverine(integration => integration.MessageStorageSchemaName = "wolverine"); // the Kit store's schema
builder.UsePouryaKitWolverine("app");
builder.Services.Replace(ServiceDescriptor.Scoped(Route<IReceipts>));         // after or before the Kit's: Replace
builder.Services.Replace(ServiceDescriptor.Scoped(Route<IIdempotencyStore>)); // Route<T> = keyed by Operation.Module
```

A Stream Module's Composition Root, abridged from `UseSpikeMartenStreams`:

```csharp
module.AddScoped<IUnitOfWork>(services => services.GetRequiredService<MartenUnitOfWork>()); // keyed by the Module
module.AddKeyedScoped<IReceipts>(module.Module, (services, _) => new MartenReceipts(services.GetRequiredService<MartenUnitOfWork>(), schema));
module.ConfigureMarten(options => { foreach (var table in MartenTables.Of(schema)) options.Storage.ExtendedSchemaObjects.Add(table); });
```

The commit, abridged from `M2.Marten/MartenUnitOfWork.cs`:

```csharp
await using var connection = await dataSource.OpenConnectionAsync(ct);   // the Unit of Work owns both, before the command
await using var transaction = await connection.BeginTransactionAsync(ct);
var options = SessionOptions.ForTransaction(transaction, shouldAutoCommit: true); // Marten commits it when it saves work
options.TenantId = tenant;                                     // when the Operation has one
var session = store.LightweightSession(options);
session.Listeners.Add(savedByMarten);                          // runs after Marten's commit only, so only when it had work
var result = await command(ct);                                // Receipts and idempotency keys run in the transaction
if (result.IsSuccess && !operation.IsRollbackOnly)
{
    var handled = await domainEvents.RunAsync(TakeRaised, ct); // TakeRaised keeps each Stream's events before the drain clears them
    try
    {
        foreach (var tracked in _tracked.Where(tracked => tracked.Pending.Count > 0))
            await tracked.AppendAsync(session, ct);            // FetchForWriting<T>(identity captured, version read, 0 when new)
        await outbox.WriteAsync(new MartenOutbox(runtime, session), handled, ct); // a new outbox per attempt
        if (!operation.IsRollbackOnly)
        {
            await session.SaveChangesAsync(ct);                // with work: commits, then flushes in Wolverine's listener
            if (!savedByMarten.Committed) await transaction.CommitAsync(ct); // no work: Marten committed nothing
            return result;
        }
    }
    catch (Exception e) when (e is ConcurrencyException or ExistingStreamIdCollisionException)
    {
        result = UnitOfWorkErrors.ConcurrencyConflict;
    }
}
// Leaving without a commit disposes the transaction, which rolls it back.
```

A Receipt is `EfReceipts`' statement on the session: `session.AdvancedSql.QueryAsync<int>("insert into ledger.pouryakit_receipts (subscription, message_id) values (?, ?) on conflict do nothing returning 1", ct, subscription, messageId)`,
one row when recorded.

## Pinned versions and licences

Read from nuget.org and the restored nuspecs on 2026-10-16.

| Package | Version | Licence |
|---|---|---|
| Marten | 9.46.0 (owner decision M1; 9.50.3 is current) | MIT |
| WolverineFx.Marten | 6.48.3 (M1, the Kit's WolverineFx line; 6.51.0 is current); depends on Marten 9.46.0 | MIT |
| Weasel.Storage | 9.41.0, new to the closure | MIT |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1, new to the closure | MIT |

Every other package of the closure, `JasperFx`, `JasperFx.Events` 2.80.2, `Weasel.Postgresql` 9.41.0 and NetTopologySuite
among them, is already in `PouryaKit.Messaging.Wolverine.EFCore`'s closure at the same version: compared from the two
projects' restore files, 86 packages against 83, the four above added and MinVer, the Kit package's build-time tool, the
only one missing; no version moved. All permissive (ADR 0002). Marten also ships an analyzer,
`JasperFx.Events.SourceGenerator`, which runs over every project that references Marten, the Stream Module included.

## Invariants for permanent tests

| Invariant | Seam | Owner |
|---|---|---|
| The store-neutral part of the Outbox write (route filter, Operation Context headers and tracestate, an Integration Event's identity and publication record, Snapshots, delayed commands) is one implementation in `PouryaKit.Messaging.Wolverine`, writing through any `MessageContext` enrolled in a store, with one scope buffer of published Integration Events both Units of Work take from | Gateway | #121, built 2026-10-18: `OutboxWriter` and `UsePouryaKitMessageStore` in `PouryaKit.Messaging.Wolverine`, which the EF Outbox and registration call; the whole existing suite, unchanged, with `NotificationTests` shown red once with the Operation Context headers off |
| Receipts and the idempotency store are selected per Module by its write model: each persistence registration on a Module's builder keys that Module's stores, the messaging package keys its Receipts the same way, a Host without Modules keeps its Host-level registrations, and the Modules registration adds the unkeyed resolvers by replacement, so registration order and repeated calls change nothing; Modularity names no Wolverine type, so the resolver is generic or registered by the messaging package | Gateway, a mixed Host | #121, built 2026-10-18: Cqrs's generic `IModuleServices.RouteByModule`; `PerModuleStoreTests.Each_module_resolves_its_own_receipts_and_idempotency_store_whatever_the_registration_order` (three orders) and `PerModuleStoreTests.A_host_without_modules_keeps_its_single_receipts_and_idempotency_store`; `PackageGraphTests` keeps Wolverine out of Modularity |
| A Marten-only Host reaches the message store's registration and the idempotency options' validation without the EF packages | Host | #121, built 2026-10-18 as far as the current packages allow: `MessageStoreWithoutEfTests` composes `UsePouryaKitMessageStore` and Cqrs's `AddPouryaKitIdempotencyOptions` with no EF registration. Note of 2026-10-18: section C keeps `WolverineFx.Postgresql`, Npgsql and Weasel out of `PouryaKit.Messaging.Wolverine` and Wolverine out of `PouryaKit.Persistence.Postgres`, so the PostgreSQL persistence call, `PersistMessagesWithPostgresql`, which the store package passes, and the store database check stay in `PouryaKit.Messaging.Wolverine.EFCore`; the Marten package of #131 reaches `WolverineFx.Postgresql` and Npgsql through `WolverineFx.Marten` for its own, unless the owner amends section C |
| One message store and one durability agent: the Marten package integrates with the Kit store's database and schema, and another schema fails the start | Host | #131, built 2026-10-18: `StreamTests.One_message_store_and_one_durability_agent_serve_both_write_models` and `StreamTests.A_marten_store_integrated_in_another_schema_than_the_message_stores_fails_the_host_when_it_is_built` (at build since the review of #131, F5) in the Proving Sample's Host with the test Stream Module Accounts |
| A Marten store on another database than the message store fails the Host when it is built | Host | #131, built 2026-10-18: `StreamTests.A_marten_store_on_another_database_than_the_message_stores_fails_the_host_when_it_is_built`, whether the connection string the registration names or a later `ConfigureMarten` moves it; the Marten store, as built, joins the EF package's `MessageStoreDatabaseCheck` as an `OutboxConnection` |
| An envelope commits with its events and is delivered through the Kit's routed queue; a moved stream and a doubly started stream are `UnitOfWorkErrors.ConcurrencyConflict` with nothing appended or sent, in both windows; appends use `FetchForWriting` at the version read, never `Append(id, expectedVersion, ...)` | Gateway | #131, built 2026-10-18: `StreamTests.An_envelope_commits_in_the_transaction_of_its_events_and_is_delivered_through_the_kits_routed_queue`, `StreamTests.A_stream_that_moved_after_it_was_read_is_the_kits_conflict_and_appends_and_sends_nothing` and `StreamTests.A_stream_started_twice_is_the_kits_conflict_and_appends_and_sends_nothing_the_second_time`, each in both windows |
| A Notification committed before the process dies is delivered after restart (a stop point after Marten's commit, before Wolverine's flush listener) | Gateway, child process | #131, built 2026-10-18: `StreamTests.A_notification_committed_before_the_process_is_killed_is_delivered_after_a_host_starts`, the child `ModularMonolith.Tests.StreamHost` killed in a store listener |
| The Unit of Work owns its transaction from before the command and opens the session over it; Receipts, Snapshot versions, publication records and idempotency keys are rows of the Module schema's tables written on it, so a concurrent duplicate waits and never runs its handler, and a failed handler leaves no Receipt, effect or Integration Event. Marten commits the transaction when it saves work; when the session holds none, the Unit of Work commits it explicitly, so a handler whose Receipt is its only write keeps it, and an idempotent command that changes no Stream keeps its key and replays its success | Delivery and Gateway | #131, built 2026-10-18: `StreamTests.A_notification_handlers_stream_effect_receipt_publication_record_and_integration_event_commit_together`, `StreamTests.A_concurrent_duplicate_delivery_waits_for_the_first_receipt_and_never_runs_its_handler`, `StreamTests.A_failed_notification_handler_leaves_no_receipt_stream_effect_or_integration_event`, `StreamTests.A_notification_handler_that_writes_nothing_keeps_its_receipt_and_a_redelivery_never_runs_it`, `StreamTests.An_idempotent_command_that_changes_no_stream_keeps_its_key_and_replays_its_success` and `StreamTests.A_snapshot_delivered_into_a_stream_module_keeps_its_version_in_the_modules_own_table` |
| A failure of Wolverine's flush after Marten's commit is not reported as the Operation's failure, as the EF outbox's flush is not (decide how the Unit of Work tells it from a failed commit) | Gateway | #131, built 2026-10-18: `StreamTests.A_send_that_fails_after_martens_commit_is_not_the_operations_failure_and_the_store_delivers_it`; how the Unit of Work tells it from a failed commit is the amendment below |
| Idempotency keys expire under `PouryaKitIdempotencyOptions`, infinite retention included, in Marten-only and mixed Hosts | Gateway | #131, built 2026-10-18: `StreamTests.A_key_reserved_longer_than_the_retention_ago_is_removed_and_a_newer_one_kept` and `StreamTests.An_infinite_retention_keeps_every_key`, each in a Host of Accounts alone and in the mixed one |
| Marten's serializer writes a typed ID as the Kit's JSON does; a Stream's identity is a Guid | Gateway | #131, built 2026-10-18: `StreamTests.A_typed_id_is_stored_as_the_kits_json_writes_it_and_a_stream_is_the_guid_its_typed_id_wraps` |
| Conjoined tenancy keyed by `OperationContext.Tenant`, Marten's default tenant for none; a delivery appends under the delivered tenant. Marten's reserved tenant id `*DEFAULT*` is the tenantless default, and `OperationContext` refuses only a blank tenant, so the Marten package refuses an explicit `*DEFAULT*` tenant at both session entry points (query and transaction), or it would read and write the tenantless streams (second review of #120, F6; the Spike does not check it) | Gateway | #131, built 2026-10-18: `StreamTests.A_stream_is_kept_and_delivered_under_the_operations_tenant_and_no_other_tenant_reads_or_writes_it`, `StreamTests.One_stream_id_is_a_stream_of_its_own_under_each_tenant_and_no_tenant_is_martens_default` and `StreamTests.A_tenant_named_as_martens_default_is_refused_by_a_command_and_by_a_query` |
| The reading-state guard refuses a Stream changed in a step that only reads, and the Operation stays rollback-only when the refusal is caught | Gateway | #131, built 2026-10-18: `StreamTests.A_query_that_changes_a_stream_marks_its_operation_rollback_only_and_nothing_commits` |
| A tracked Stream's identity is captured when the Unit of Work begins tracking it: the append goes to that identity, and a step that only reads and changes it is refused | Gateway | #131, built 2026-10-18: `StreamTests.A_query_that_points_a_tracked_stream_at_another_while_its_events_are_pending_is_refused_and_nothing_commits` |
| Feature code of a Stream Module names no type from `Marten*`, `Weasel*`, `Wolverine*` or `JasperFx*` but the source generator's evolver types, with an offending Module that names `IDocumentSession` | Structure | #131, built 2026-10-18: `FeatureCodeTests.Feature_code_of_a_stream_module_names_no_marten_weasel_wolverine_or_jasperfx_type_but_the_generated_evolvers`, with the offending Module `Offending.Stream` |
| A Stream aggregate is an internal type with public `Apply`/`Create` methods | Structure, or the Gateway test that folds one | #131 and #132; #131's built 2026-10-18: `StreamTests.A_typed_id_is_stored_as_the_kits_json_writes_it_and_a_stream_is_the_guid_its_typed_id_wraps` folds the internal `Account`, red once with an internal `Apply`; #132's built 2026-10-18: the Proving Sample's `Ledger` folds its internal `Account` in `LedgerTests.Entries_append_to_an_account_through_ledgers_gateway_and_its_projection_folds_them_for_its_tenant_only` |
| `Ledger` shows one command that appends, one projection that reads and one Integration Event to another Module through the existing messaging, with its Notification Handler's effect, Receipt and Integration Event in one commit | Gateway | #132, built 2026-10-18: `PostEntry` appends and `GetAccount` folds through `IStreams.LoadAsync`, the Kit offering no projected document (`LedgerTests.Entries_append_to_an_account_through_ledgers_gateway_and_its_projection_folds_them_for_its_tenant_only`, and over HTTP with a second opening as `ConcurrencyConflict` in `LedgerTests.Over_http_an_account_opens_at_its_location_takes_an_entry_and_reads_and_a_second_opening_is_the_kits_conflict`); `ActivateAccountNotificationHandler` appends the activation and publishes `AccountOpenedV1` in one commit with its Receipt, which Billing receives (`LedgerTests.An_opened_account_reaches_billing_from_commits_that_hold_each_stream_effect_with_its_receipt_and_envelope`, `LedgerTests.A_failed_receiving_handler_leaves_no_receipt_and_no_effect`) |

## Consequences

- ADR 0003 and ADR 0004 carry the one-commit-owner amendment of 2026-10-16. The packages follow in #121, #131 and #132,
  which turn the invariants above into tests over the merged code; nothing of `spikes/marten-two-store/` is merged.
- The Kit runs no Wolverine-generated handler for a Stream, as for a row (ADR 0002, 0006).
- Plan I.4's internal `Apply` methods are corrected here, and a Stream's Guid identity recorded, not in the plan, which
  keeps its record; its I.3 row carries a dated note pointing here.
- Plan I.4 has `MartenReceipts` keep Receipts, Snapshot versions and the publication record as Marten documents on the
  same session. This record rejects documents ("Rejected options") and keeps them as rows of the Module schema's tables,
  written on the session's transaction; the I.3 note says so too.

## Amendment (2026-10-18, ticket #131: the packages as built)

The decision above stands. `PouryaKit.Persistence.Marten` and `PouryaKit.Messaging.Wolverine.Marten` build it on Marten
9.46.0 and WolverineFx.Marten 6.48.3 (M1), and the invariants above are the tests their rows name.

- **A send that fails after Marten's commit is not the Operation's failure.** Wolverine's send runs in the listener its
  `MartenOutbox` adds to the session, inside `SaveChangesAsync` and after Marten's commit, so an exception from it leaves
  `SaveChangesAsync` as a failed commit would. The Unit of Work tells the two apart by the commit itself: it adds its own
  listener to the session when it opens it, before the outbox adds its own, and Marten calls the session's listeners in
  order after its commit only, so that listener records that Marten committed. An exception after it is logged as the
  warning `NotSent` and the Operation returns its success, as the EF outbox's send does (ADR 0004): the change committed,
  and the envelopes stay in the message store, which delivers them after the inbox stale time or when a Host starts. An
  exception before it is the commit's and propagates, a concurrent change as `UnitOfWorkErrors.ConcurrencyConflict`. A
  store listener of the application (`StoreOptions.Listeners`) runs before the session's, so one that throws after the
  commit is still reported as a failure; the Kit registers none.
- **The packages' references.** Persistence.Marten references Cqrs and Persistence, and `Marten` and `Npgsql`: the Outbox
  step is a seam, `IStreamOutbox`, which Messaging.Wolverine.Marten fills with `OutboxWriter` over a `MartenOutbox`, so
  the I.3 row's `→ Messaging` is not taken; `Npgsql` 10.0.3 is the Kit's line over the 9.0.4 Marten brings, since the
  Unit of Work runs the Kit's statements on Npgsql itself, recorded for the owner to confirm. `UnitOfWorkErrors` moved
  from Persistence.EFCore to Persistence, so both Units of Work fail a conflict with the one Error.
- **One message store.** `UsePouryaKitMarten(connectionName, schema)` registers the Host's one Marten store, integrated
  with the message store in `schema`. In a Host with EF Core Modules it joins the store `UsePouryaKitWolverine(connectionName)`
  of the EF package declared, which comes first, since Wolverine's `UseWolverine` runs once, and `UsePouryaKitMessageStore` refuses a second declaration, naming that order; in a Host of Stream Modules
  alone it declares the store itself through `UsePouryaKitMessageStore`, the Marten integration's own store being the
  Host's one, on the database Marten's store is built with (since the review of #131; the note on the third #121 row
  foresaw `PersistMessagesWithPostgresql`). In a Host of both, the Marten store as built, the connection string of the
  data source Marten made and the integration's schema, joins the EF package's `MessageStoreDatabaseCheck` as an
  `OutboxConnection` of Messaging.Wolverine, so another database or another schema fails the Host when it is built,
  whatever configured the store (review F1 and F5; the row's "another schema fails the start" holds, earlier).
- **Receipts and keys per Module.** `AddPouryaKitStreams(schema)` on a Module's builder registers the Module's Marten Unit
  of Work and keys its idempotency store, and records the Module in `StreamModules`, whose `ForEach` the messaging
  package uses to key `MartenReceipts` for each Stream Module, whichever registration comes first. Receipts, Snapshot
  versions, publication records and keys are rows of the Module schema's tables, of the EF tables' shapes, which Marten
  creates with the store as extended schema objects; the statements are the EF packages', `ReceiptStatements` in
  Messaging.Wolverine and `IdempotencyStatements` in Persistence, which both references allow.
- **The Stream API.** `StreamAggregate.Id` is the Guid Marten folds, and `IStreams.LoadAsync<TStream>(TypedId<Guid>)`
  takes the typed ID that wraps it. An Operation whose tenant is `*DEFAULT*` is refused at both session entry points
  with an `InvalidOperationException`.
- **Limits recorded.** A Stream publishes no Snapshot: the Marten Outbox passes `OutboxWriter` none. Marten creates its
  own event tables when an event is first appended, and the Module tables when the Host starts. Realtime, `IUnitOfWorkConnection`
  and number sequences stay the EF Unit of Work's (spec #115, "Out of Scope").
