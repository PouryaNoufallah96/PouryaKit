# PouryaKit.Messaging.Wolverine.EFCore provenance

Adopted implementations in `PouryaKit.Messaging.Wolverine.EFCore`. Format and rules: [README](README.md).

The Outbox commit (`WolverineOutbox`, the Unit of Work's step 5) is the Kit's own code, adapted from Spike S1 (ticket #2, ADR 0004, tag `spike/s1-atomic-commit`, `spikes/s1-atomic-commit/S1.Host/SpikeUnitOfWork.cs`): publish through Wolverine's EF Core outbox inside the Unit of Work's transaction, save, commit, then flush. It creates a new `DbContextOutbox` enrolled with the Unit of Work's DbContext for every commit attempt, rather than taking the scope's `IDbContextOutbox<TDbContext>`, so an outbox is never reused after a rollback by construction; both build the same `EfCoreEnvelopeTransaction`. The Receipts (`EfReceipts`, `ReceiptTable`) are new Kit code (plan section B.2, "Receiving receipt"; ADR 0006, "Where the Receipt attaches"), and so is `PublishedIntegrationEvents`, which hands the Integration Events an Operation published to the Outbox, and the publication record that refuses a second Integration Event of one type for one Domain Event, kept in the publishing Module's Receipt table (ticket #16). Building the Snapshots of the changed aggregates in the Outbox write, the Unit of Work's step 4 (`SnapshotStep`), and the Snapshot versions a subscription applied (`EfReceipts.RecordVersionAsync`, `SnapshotVersionTable`) are new Kit code, ticket #18. The policy block is recorded with [PouryaKit.Messaging.Wolverine](PouryaKit.Messaging.Wolverine.md).

## Package dependencies

Checked on nuget.org on 2026-10-05 (ticket #15); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `WolverineFx.EntityFrameworkCore` 6.46.0, `MIT`, with `Weasel.EntityFrameworkCore` 9.41.0, `MIT`. On `net10.0` it asks for EF Core 10.0.4 or later and restores against 10.0.12. It brings `Microsoft.EntityFrameworkCore.Design` 10.0.4 and its `Microsoft.CodeAnalysis` 5.0.0 packages at run time, all `MIT`, as plan section C expects.
- `WolverineFx.Postgresql` 6.46.0, `MIT`, with `WolverineFx.RDBMS` 6.46.0, `Weasel.Core` 9.41.0 and `Weasel.Postgresql` 9.41.0, all `MIT`.
- The package's restored closure is 82 packages: 76 `MIT`, 3 `BSD-3-Clause` (`NetTopologySuite` 2.5.0, `NetTopologySuite.IO.PostGis` 2.1.0, `Polly.Core` 8.6.5), 2 `PostgreSQL` (`Npgsql` and `Npgsql.NetTopologySuite` 9.0.4, which a Host with `PouryaKit.Persistence.Postgres` lifts to `Npgsql` 10.0.3) and 1 `Apache-2.0` (`NewId` 4.0.1).
- It does not reference `WolverineFx.RuntimeCompilation`; the Host does (ADR 0004, "Defaults chosen", 2).
