# PouryaKit.Persistence.Postgres provenance

Adopted implementations in `PouryaKit.Persistence.Postgres`. Format and rules: [README](README.md).

The reader of unique violations (`PostgresUniqueConstraintReader`, ticket #11) is new Kit code, so it has no record here, and so is the check of each named connection string at start (`PostgresConnectionsCheck`, ticket #62), which uses Cqrs's `ICompositionCheck` and `IConfiguration` through `PouryaKit.Persistence.EFCore` and EF Core, with no reference of its own.

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. Neither is reciprocal or commercial (ADR 0002), and neither brings Wolverine.

- `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `PostgreSQL`; it brings `Npgsql` 10.0.3, also `PostgreSQL`.
- `EFCore.NamingConventions` 10.0.1, `Apache-2.0`.

## PostgreSQL registration with a schema per Module

- **Kit files:** `src/PouryaKit.Persistence.Postgres/PostgresServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Persistence.EfCore.Postgres/EfCorePostgresExtensions.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #8
- **Intentional differences:**
  - No `EnableRetryOnFailure`: the Unit of Work owns an explicit transaction, and a retry needs a new scope (ADR 0003, ADR 0004).
  - No global `Npgsql.EnableLegacyTimestampBehavior` switch, no pgvector, and no interceptors: audit and soft delete are Unit of Work steps (ticket #11), and the Outbox is written in the Unit of Work's transaction by `PouryaKit.Messaging.Wolverine.EFCore` (ticket #15).
  - The connection string is named, `Name=ConnectionStrings:<connectionName>`, and resolved from the Host's configuration by EF Core, instead of being passed in as a string.
  - The migrations history table is put in the Module's schema, and the DbContext base takes its default schema from it, so one `schema` argument sets both. The migrations assembly is EF Core's default, the DbContext's own.
  - snake_case names through `EFCore.NamingConventions`, set here rather than in a shared options helper.
  - The DbContext becomes the Module's Unit of Work and the generic repository's store (`AddPouryaKitEfCore`).
