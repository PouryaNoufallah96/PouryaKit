# One unit of work owns every commit; handlers never save

A single unit of work decides each operation's outcome in a fixed order: roll back on any failure
(including a failed nested command), run domain events until none remain and check for failure again, prepare persistence (audit,
soft delete, one version bump per changed aggregate), build one snapshot per changed aggregate, then
save and commit once through the outbox. Every inspected codebase let handlers save or let a failed
`Result` commit; this ordering exists to make those defects impossible rather than discouraged.

## Consequences

Audit and soft delete are steps of the unit of work, not `SaveChanges` interceptors, because nothing
may change published state after snapshots are built. Whole-operation retries must start a new scope.
