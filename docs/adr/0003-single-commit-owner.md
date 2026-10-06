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

The decision above stands. Tickets #9 to #11 and #18 fixed its details, which stored versions, Snapshots and Feature
code now depend on.

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
