# PouryaKit.Persistence.Marten provenance

Adopted implementations in `PouryaKit.Persistence.Marten`. Format and rules: [README](README.md).

The package has no adopted implementation. The Marten Unit of Work, `src/PouryaKit.Persistence.Marten/MartenUnitOfWork.cs`, its idempotency store and cleanup, `IStreams`, `StreamAggregate`, the Host's store registration and the composition check that the Unit of Work has an Outbox in a Host that has one (`StreamOutboxCheck`, PR #143 review) are Kit code, built on the shape Spike #120 proved and ADR 0011 records (`spikes/marten-two-store/` at `d03cc740`, tag `spike/marten-two-store`, not merged), with Marten's standard calls: `SessionOptions.ForTransaction`, `FetchForWriting`, `AggregateStreamAsync`, `IDocumentSessionListener` and extended schema objects. The plan's I.3 row names only `woo@149a3cf src/EventStoreDB/Repository/EventStoreDBRepository.cs:7-16` (`Find`/`Add`/`Update(expectedVersion)`) and `CloudNativeKit@e81d65e src/CloudNativeKit.Abstractions/Persistence/EventStore/ExpectedStreamVersion.cs:3-7` for their names; no code was taken from either (ticket #131).

## Package dependencies

Checked on nuget.org and in the restored nuspecs on 2026-10-18 (ticket #131). None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure, analyzers included.

- `Marten` 9.46.0, `MIT`, owner decision M1 (9.50.3 is current). It brings `JasperFx` and `JasperFx.Events` 2.80.2, `Weasel.Core`, `Weasel.Postgresql` and `Weasel.Storage` 9.41.0, `Microsoft.IO.RecyclableMemoryStream` 3.0.1, all `MIT`, `NetTopologySuite` 2.5.0, `BSD-3-Clause`, and `Npgsql.NetTopologySuite` 9.0.4, `PostgreSQL`, and ships the `JasperFx.Events.SourceGenerator` analyzer, which runs in every project that references the package and generates at compile time the code Marten folds a Stream with at run time.
- `Npgsql` 10.0.3, `PostgreSQL`, the Kit's line (`PouryaKit.Persistence.Dapper` and `PouryaKit.Realtime` reference it), over the 9.0.4 Marten brings: the Unit of Work runs the Kit's Receipt and idempotency statements on its Npgsql connection itself. Recorded approved by the owner on 2026-10-23 (spec #115), 2026-10-18.

`PouryaKit.Persistence.Marten` also references `PouryaKit.Cqrs` and `PouryaKit.Persistence`, as plan section C lists since ticket #131. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).
