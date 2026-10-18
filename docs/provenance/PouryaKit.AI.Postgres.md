# PouryaKit.AI.Postgres provenance

Adopted implementations in `PouryaKit.AI.Postgres`. Format and rules: [README](README.md).

The vector store (`src/PouryaKit.AI.Postgres/PostgresVectorStore.cs`, `src/PouryaKit.AI.Postgres/IVectorStore.cs`), the migration helper of its collections (`src/PouryaKit.AI.Postgres/VectorCollectionTable.cs`, `src/PouryaKit.AI.Postgres/VectorCollection.cs`) and its registration (`src/PouryaKit.AI.Postgres/VectorStoreServiceCollectionExtensions.cs`) are new to the Kit, decided by spec #115's AI decision and plan I.4 (ticket #138). They follow the Kit's own shapes: the write on the Unit of Work's connection and the migration helper of `PouryaKit.Persistence.Postgres`'s number sequences, and the tenant of the Operation as the Dapper lease binds it. Plan I.4 excludes the reference project's vector store, on SQL Server with a global vector type handler, so none of it is adopted.

## Package dependencies

None of its own: `PouryaKit.AI.Postgres` references `PouryaKit.AI`, for the Host's embedding generator, and `PouryaKit.Persistence.Postgres`, whose Npgsql EF provider brings `Npgsql` 10.0.3 (`PostgreSQL`) and EF Core's migrations, as plan section C records (ticket #138). `PackageGraphTests` holds it to those, `LicenceTests` to its closure, and keeps the `Pgvector` client and the package itself out of every other Kit package's closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

Checked on nuget.org on 2026-10-18 and not referenced:

- `CommunityToolkit.VectorData.PgVector` 1.0.1, `MIT`, the latest stable, with a `net10.0` asset, which plan I.4 names. Its collection writes on a connection it opens from its own data source (`CommunityToolkit/AI` at `126f5ae78aef91192d161088737e3c00acc3a23c`, `MEVD/src/PgVector/PostgresCollection.cs`, `UpsertAsync`), so a stored embedding would commit outside the Operation's Unit of Work, which ADR 0003 refuses; its tables come from `EnsureCollectionExistsAsync` at run time, not from a migration; and its tenant filter would be a caller's expression, not the Operation's.
- `Pgvector` 0.3.2, `MIT`, the latest stable, the client type mapping of pgvector for Npgsql. A mapping is a data source's, and the Unit of Work writes on the connection of the Module's DbContext, which the package does not build; it sends each embedding as a `real[]`, which pgvector casts to its `vector` on assignment and with `::vector` in a search, so no mapping is needed.

## The pgvector server extension

The `vector` extension is the PostgreSQL server's, pgvector (`PostgreSQL` licence), not a package: `CreateVectorCollection` creates it in a migration where the database has none, and a Host that never calls it takes none. The tests run PostgreSQL with pgvector from the `docker.io/pgvector/pgvector:0.8.7-pg18` image, recorded in [the Proving Sample's record](ModularMonolith.md).
