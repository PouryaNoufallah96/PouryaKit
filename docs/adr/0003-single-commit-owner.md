# One unit of work owns every commit; handlers never save

A single unit of work decides each operation's outcome in a fixed order: roll back on any failure
(including a failed nested command), run domain events until none remain and check for failure again, prepare persistence (audit,
soft delete, one version bump per changed aggregate), build one snapshot per changed aggregate, then
save and commit once through the outbox. Every inspected codebase let handlers save or let a failed
`Result` commit; this ordering exists to make those defects impossible rather than discouraged.

## Consequences

Audit and soft delete are steps of the unit of work, not `SaveChanges` interceptors, because nothing
may change published state after snapshots are built. Whole-operation retries must start a new scope.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. Its details, which stored versions, Snapshots and Feature
code now depend on, are as follows.

- **The sequence as built** (`src/PouryaKit.Persistence.EFCore/EfUnitOfWork.cs`): begin the transaction; run the
  command; on a failed Result or a rollback-only Operation, roll back; drain the Domain Events; check rollback-only
  again; prepare persistence; write the Outbox, which builds the Snapshots, so the Snapshot step sits inside the Outbox
  write; check rollback-only once more, because a Snapshot mapper may have ignored a refused nested command; save;
  commit; send. Preparing persistence converts soft-deletable removals to marks, then resolves EF Core's cascades of
  the removals that stay deletions for tracked entries, so the save changes no tracked state the Snapshots did not see.
  Database cascades may still delete unloaded dependents during the save
  (`PersistencePreparationTests.Removing_an_aggregate_resolves_its_cascades_before_the_save_so_the_save_changes_nothing_further`).
- **Code that only reads.** `Operation.MayChangeState` is false in a query, an authorizer or a validator at any depth,
  and inside an `Operation.OnlyReading` step. Each Snapshot mapper runs in such a step.
  There a nested command throws, and so do `IUnitOfWorkConnection.Join()` and the repository's `AddAsync` and
  `Remove`: a write there would escape the Operation's outcome, since a failed query Result alone marks nothing
  (`ReadingStepTests`, `DapperTests.A_query_cannot_join_the_unit_of_work_connection`,
  `RepositoryTests.A_query_sent_from_a_command_cannot_add_an_aggregate_through_the_repository`).
  The EF Unit of Work also guards direct changes to tracked objects when a query, authorization and validation step,
  or default `OnlyReading(step)` ends, including failed Results and exceptions. It compares against current pending
  command state and captures newly loaded entities when they begin tracking. The shared comparer checks values,
  states, tracked membership, complex collections, and pending
  Domain Event identity and order. A difference marks the Operation rollback-only before throwing, so an outer handler
  cannot catch the exception and commit the mutation (`ReadOnlyStateTests`). Snapshot mapper scopes keep the write
  guards but defer tracked-state comparison to one check after the whole batch. A difference then throws during commit
  and rolls back the transaction. Changes restored before the relevant boundary or batch ends are outside this
  comparison's guarantee.
- **The drain** is the public `DomainEventDrain` in `PouryaKit.Cqrs`, which any `IUnitOfWork` calls. It runs at most
  `DomainEventDrain.MaxDepth` (10) rounds and throws when Domain Events remain, since a cycle of reactions is a bug.
  The handlers of one Domain Event run one at a time in the ordinal order of their full type names, so renaming a
  handler can change the order; a handler runs only for its exact Domain Event type, never for a derived one. The
  drain stops at the first failure (`DomainEventTests`).
- **Versions.** Each changed aggregate's version is the value the Operation read plus one, whatever happened to it in
  between: a new aggregate commits as 1, and one deleted outright gets its last committed version plus one, which its
  Tombstone carries (`PersistencePreparationTests.The_version_advances_exactly_once_for_each_operation_that_changes_any_part_of_the_aggregate`,
  `SnapshotTests.Removing_an_aggregate_deleted_outright_publishes_its_tombstone_with_a_newer_version`). Receivers
  compare these versions, so changing the rule changes what they apply.
- **What changes a holder.** A changed child counts for the aggregate that holds it now and the one that held it when
  loaded, so moving a child advances both. The holder must be tracked in the Operation, loaded or added: a changed part
  whose holder is not tracked throws, because that aggregate's version could not advance and a concurrent change would
  not conflict (`src/PouryaKit.Persistence.EFCore/PersistencePreparation.cs`;
  `PersistencePreparationTests.A_page_moved_to_another_folder_advances_and_stamps_both_folders_once`,
  `PersistencePreparationTests.Changing_a_part_of_an_aggregate_the_operation_did_not_load_throws_and_writes_nothing`).

## Amendment (2026-10-08: a write no Unit of Work would commit is refused)

The decision above stands. Since only the Unit of Work commits, a write made where no Unit of Work runs is never
saved. The 2026-10-06 amendment's "Code that only reads" bullet stays as written and still holds
for code that only reads; where it and this amendment differ, this one holds: `Operation.MayChangeState` alone no
longer decides whether the repository writes, because it is true in a scope that runs no command at all.

- **The signal.** `Operation.IsUnitOfWorkActive` (`src/PouryaKit.Cqrs/Operation.cs`) is public, with a private setter.
  It is the one signal of the window in which the Unit of Work of the Operation's outermost command runs: true from
  the dispatcher's call of `IUnitOfWork.RunAsync`, made once that command's authorizers and validator have passed,
  until that call has returned, committed or rolled back. The command's handler, every command nested in it, its Domain
  Event Handlers, a delivery's Notification Handler or Integration Event Handler and a delayed command's run, each of
  which the Kit runs inside a command of its own, all run inside the window. It is false before the Operation begins,
  in a begun scope that sends no command, in a query sent on its own, in the outermost command's authorizers and
  validator, and once the Unit of Work has ended. It is public because a Kit
  package outside `PouryaKit.Cqrs` must be able to refuse a write that no Unit of Work would commit, and
  [ADR 0007](0007-operation-context-set-by-the-scope-opener.md) keeps Cqrs's `InternalsVisibleTo` to the trusted
  restore alone (`TrustedRestoreTests`). `Operation` already exposes `MayChangeState`, `Module` and `Context` the same
  way: readable by any package, set only by Cqrs. Feature code has no need to read it.
- **The rule.** A write that only a Unit of Work's commit persists is refused, with an `InvalidOperationException`
  that names who may write, when it is made while no Unit of Work is active. Each write below checks `MayChangeState`
  first, so code that only reads keeps the message that names its own mistake. The Kit applies the rule in three
  places:
  - `IDelayedCommands.Schedule`: a command scheduled outside a Unit of Work would never be written to the Outbox
    (`DelayedCommandTests.Scheduling_in_a_scope_of_a_host_of_modules_that_runs_no_unit_of_work_throws_and_stores_nothing`,
    `DelayedCommandTests.Scheduling_in_a_scope_of_an_application_without_modules_that_runs_no_unit_of_work_throws`).
  - `IRepository.AddAsync` and `Remove`: the DbContext would track the change and nothing would save it
    (`RepositoryTests.A_repository_write_in_a_scope_of_a_host_of_modules_that_runs_no_unit_of_work_throws_and_saves_nothing`,
    `SingleModuleTests.A_repository_write_in_a_scope_that_runs_no_unit_of_work_throws_and_saves_nothing`). Every other
    member of the repository reads and needs no Unit of Work, so a query sent on its own reads through it as before.
  - `IUnitOfWorkConnection.Join()`, which has refused it since it was written: it reads the DbContext's current
    transaction, not this signal, and the two agree inside `RunAsync`
    (`DapperTests.Joining_the_unit_of_work_connection_where_no_unit_of_work_is_active_throws`).

  `IIntegrationEventPublisher.Publish` needs no check of its own: it is refused anywhere but a Notification Handler's
  delivery, which runs inside a Unit of Work. In a Host of Modules, a scope whose Operation never began fails earlier,
  when the repository is resolved, because its Unit of Work is its Module's and the scope names none
  (`RepositoryTests.A_repository_is_refused_in_a_scope_of_a_host_of_modules_whose_operation_never_began`).
- **What the rule cannot cover.** A change to an aggregate loaded through the repository, or a Domain Event raised on
  one, in a scope that runs no command, is still lost without a word: no call of the Kit is made, so there is nothing
  to refuse, and the tracked-state comparison runs only at the end of a query, a check or a reading step. The Kit's own
  write seams, `IReceipts`, the Outbox buffers (`IIntegrationEventOutbox`, `IDelayedCommandOutbox`) and
  `IUnitOfWork.DbContext`, stay unguarded, because the structure test closes them to a Module
  (`FeatureCodeTests.A_module_that_saves_commits_writes_in_bulk_or_runs_a_kit_step_is_rejected`). A Host's own
  assembly is not judged by that test, so code there can still reach them.
  *A Module of a Host of Modules cannot reach the first case: an endpoint's scope names no Module and resolving a
  repository there throws, the Gateway opens and disposes its own scope around one dispatch, and the structure test
  rejects a Module that begins an Operation. It stays open in an application without Modules, where an endpoint or a
  service can take a repository itself. No structure rule was added: the Kit's structure tests judge Modules of a Host
  of Modules, where it would refuse nothing, and a service that a handler calls is a legitimate consumer it would
  refuse.*
  *In an application without Modules, where the scope sends no command, the change is lost without a word, as written:
  it is dropped and nothing throws
  (`SingleModuleTests.A_change_to_a_loaded_aggregate_in_a_scope_that_sends_no_command_is_dropped_without_a_word`). A
  query sent after the change is not refused, and the change is still dropped: the tracked-state comparison's
  baseline is the query's start, which already holds the change
  (`SingleModuleTests.A_change_to_a_loaded_aggregate_is_not_refused_by_a_query_sent_after_it_and_is_still_dropped`).
  But a command the same scope sends after the change, and that succeeds, saves it: its commit saves the whole
  DbContext, so the change is committed by a command that never made it
  (`SingleModuleTests.A_change_to_a_loaded_aggregate_is_saved_by_the_commit_of_a_command_sent_after_it`). The three
  tests change a value; none raises a Domain Event. The monolith template's own files are judged: its Composition
  Root, its endpoints, the Host's `Program.cs` and `AppHost.cs` (which now holds the composition `Program.cs` held) and
  the smoke test both templates share; read as text, none of them takes a repository, as a parameter, a field or a local of
  `IRepository<,>` or of an interface the Proving Sample derives from it, or from a service provider
  (`TemplateTests.None_of_the_monoliths_own_files_takes_a_repository`; the rule's summary lists what text cannot
  show). The template's business code is the Proving Sample's and this rule does not judge it, so it never has to
  tell a handler from a service that a handler calls. What stays open: code an application adds after `dotnet new`
  is not judged, since no package ships structure rules.*
- **The alternatives rejected.**
  - *Widening Cqrs's `InternalsVisibleTo`* to every package that guards a write, with the signal kept internal: it
    would undo ADR 0007's rule that the list serves the trusted restore alone, and `TrustedRestoreTests` would have to
    allow each package a second internal member.
  - *Folding the window into `MayChangeState`*, false wherever no Unit of Work runs: it would change what every
    existing check of code that only reads means. A scope that runs no command would be told that "code that only
    reads" asked to write, which names a mistake nobody made, and the property's own contract, that a command is
    refused where it is false, would no longer hold: the outermost command is sent exactly where no Unit of Work runs
    yet.
  - *A guard in the repository's constructor or its read members*: a query's handler injects the repository and reads
    through it with no Unit of Work, and so does a specific repository built in a scope that runs no command.

## Amendment (2026-10-10: the Unit of Work's DbContext is reached through a static accessor)

The decision above stands, and so does the amendment on a write no Unit of Work would commit. Only the shape of one of the Kit's write seams
changes. The DbContext an EF Core Unit of Work saves and commits was an extension property on `IUnitOfWork`, the
amendment's `IUnitOfWork.DbContext`. Every repository has `PouryaKit.Persistence.EFCore` and an `IUnitOfWork` in scope,
so code completion offered Feature code the DbContext this decision reserves for the Unit of Work, and only the
structure test caught a call, after compilation.

- **Now** `EfUnitOfWorkDbContext.Of(unitOfWork)`, a plain static accessor in the same package and namespace, which no
  completion on `IUnitOfWork` lists. Its Kit callers, the Receipts and the idempotency store, are unchanged otherwise.
- **The structure test still closes it to a Module.** `FeatureCodeTests.A_module_that_saves_commits_writes_in_bulk_or_runs_a_kit_step_is_rejected`
  names the offending Module's call of `EfUnitOfWorkDbContext.Of`, red with the accessor taken off the rule's Kit
  steps.

## Amendment (2026-10-10: a Snapshot never carries a value the database generates)

The decision above stands. The Snapshot step builds each Snapshot from the prepared state, before the save, so a value
the database writes during the save is not in it: a default value SQL or a computed column on a property a mapper reads
would commit one value and publish another, against this decision's intent that what an Operation publishes is the
state it commits.

- **Refused at commit, beside the temporary key.** `SnapshotStep.Build` refuses a non-key property whose value the
  database generates, a default value SQL, a computed column or a `ValueGenerated` other than `Never` that no
  client-side value generator sets for good when the entity is added, anywhere in the mapped graph of an aggregate with a declared Snapshot: the aggregate, the types it owns and the children it holds
  through a navigation, their derived types included, as the prepare step attributes their changes. The
  `InvalidOperationException` names the entity and the property, and nothing commits. The graph is read from the model
  once per DbContext type. The Kit's own conventions, Guid version 7 keys and Kit-set versions and audit fields, never
  meet it, and keys stay allowed.
  *Corrected on 2026-10-10: the graph is read once per model, the `IModel` instance the
  DbContext exposes, not once per DbContext type. EF lets one DbContext type carry several models, one given with
  `UseModel` or one per model cache key, and a read cached by type gave a second model the first one's aggregate roots
  and generated properties, so the second model's refusals could be skipped. `SnapshotStep` keeps the read in a
  `ConditionalWeakTable` keyed by that model, which holds an entry only as long as the model. Proven by
  `SnapshotTests.A_snapshot_is_refused_by_the_model_its_dbcontext_uses_when_one_dbcontext_type_has_two_models`, red
  with the type key.*
  *Refined on 2026-10-10: the read keeps the properties whose values the database may generate, and
  whether a client-side value generator sets one for good is asked of the committing DbContext's
  `IValueGeneratorSelector` at each commit. Two contexts given one model can replace the selector differently, and the
  first one's answer was kept for both. The selector caches what it selects: 1,000 builds of a Snapshot whose aggregate
  has such a property took about 22 ms before and after. Proven by
  `SnapshotTests.A_snapshot_is_refused_by_the_value_generator_selector_its_dbcontext_uses_when_two_share_a_model`, red
  with the selector's answer cached.*
  *Extended on 2026-10-11: a child's key is checked too, so the
  refusal is no longer of non-key properties alone, and "keys stay allowed" holds for the aggregate's own keys only.
  Every key was left out, so a child whose key an identity column numbers was published with the key its entity held before the
  save, while its row committed the database's number. The aggregate's own keys stay left out, its primary key
  refused while it is temporary. A child's key generated on add is refused unless the generator the committing
  DbContext's selector returns for it, which EF uses for a key whether or not its mapping names one, gives values
  that are not temporary, as a `Guid` key's does. A shadow key is left out, since no mapper can read it: refusing the
  one EF gives an owned collection by default would name a property the author never declared. Proven by
  `SnapshotTests.A_snapshot_is_refused_when_the_database_generates_the_key_of_a_child_in_its_aggregates_graph`, red
  with child keys left out, and
  `SnapshotTests.A_snapshot_of_an_aggregate_whose_owned_collection_has_efs_default_key_is_built`, red with shadow keys
  checked.*
- **Proven against PostgreSQL** by `SnapshotTests.A_snapshot_of_an_aggregate_whose_database_generates_a_value_it_maps_fails_the_operation_that_saves_it`
  (a default value SQL on the root) and
  `SnapshotTests.A_snapshot_of_an_aggregate_whose_database_generates_a_value_of_an_owned_part_it_maps_fails_the_operation_that_saves_it`
  (a computed column on an owned part), each red with the refusal off: the Snapshot published no value while the row
  committed the database's. `SnapshotTests.A_snapshot_of_an_aggregate_whose_value_a_client_side_generator_sets_carries_the_value_committed`
  shows a client-side generator's value is not refused.

## Amendment (2026-10-16: each write model has one commit owner)

The decision above stands, for each write model: each write model has one commit owner; the EF Unit of Work owns rows
and commits through the EF outbox; the Marten Unit of Work owns a Stream and commits through Wolverine's Marten outbox;
no Operation commits through both. A Module declares one write model, and the Unit of Work its Composition Root
registers, which `AddPouryaKitModules` keys by the Module, decides every Operation of that Module in the order above.
The steps that read EF Core's tracked rows, preparing persistence and building the Snapshots of the changed aggregates,
are the EF Unit of Work's; the Marten Unit of Work appends the Domain Events it drained to each Stream at the version it
read. The commit shape and the invariants are in [ADR 0011](0011-each-write-model-has-one-commit-owner.md).
