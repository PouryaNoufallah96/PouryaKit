# PouryaKit.Persistence.Dapper provenance

Adopted implementations in `PouryaKit.Persistence.Dapper`. Format and rules: [README](README.md).

The check of the read connection string when the Host's container is built (`ReadConnectionCheck`, ticket #62) is new Kit code, so it has no record here.

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002), and none brings EF Core.

- `Dapper` 2.1.89, `Apache-2.0`; no dependencies on `net10.0`.
- `Npgsql` 10.0.3, `PostgreSQL`, the ADO.NET driver alone. It is the version `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 already resolves, so a Host has one driver. It brings `Microsoft.Extensions.Logging.Abstractions` 10.0.0, `MIT`, which brings `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12, `MIT`; no dependencies on `net10.0`. It is a reference beyond the package's row in plan section C, approved by the owner on 2026-10-05: `AddPouryaKitDapper` is a registration call on a Module's builder, an `IServiceCollection`, so the package references it directly at the version the Kit pins, instead of compiling against the 10.0.0 that Npgsql brings.
- `Microsoft.Extensions.Configuration.Abstractions` 10.0.12, `MIT`. It is a reference beyond the package's row in plan section C (Domain; Dapper, Npgsql), approved by the owner on 2026-10-05: `AddPouryaKitDapper` takes a connection name and reads `ConnectionStrings:<name>` from the Host's configuration, as `UsePouryaKitPostgres` does, and Npgsql's closure has no configuration abstractions. It brings only `Microsoft.Extensions.Primitives` 10.0.12, `MIT`, and no EF Core.

## Read-connection lease

- **Kit files:** `src/PouryaKit.Persistence.Dapper/IReadConnectionFactory.cs`, `src/PouryaKit.Persistence.Dapper/ReadConnectionFactory.cs`, `src/PouryaKit.Persistence.Dapper/DapperServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Persistence.EfCore.Postgres/DbConnectionFactory.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Connection/IDbConnectionFactory.cs`, `src/BuildingBlocks/BuildingBlocks.Persistence.EfCore.Postgres/EfCorePostgresExtensions.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #9
- **Intentional differences:**
  - In the Dapper package, which references no EF Core, instead of the EF Core PostgreSQL package (plan section B.2, "Dapper reads").
  - One method, `OpenAsync`, instead of a synchronous and an asynchronous `GetOrCreate…` pair: every call opens a new lease, so nothing is "got".
  - It checks the cancellation token before leasing: Npgsql returns an idle pooled connection without looking at the token.
  - The factory owns its `NpgsqlDataSource` and is one singleton per Host, so the Host's container disposes the pool, and no `NpgsqlDataSource` is registered for other packages to collide with. The data source is plain: no pgvector.
  - The connection string is named and read from the Host's configuration. A second Module naming the same connection shares the factory and its pool; one naming a different connection fails at registration.
  - A write that must commit with the Operation does not use this connection; it joins the Unit of Work through `IUnitOfWorkConnection` in `PouryaKit.Persistence`, which Cybernod lacks.

## Typed-ID type handler

- **Kit files:** `src/PouryaKit.Persistence.Dapper/TypedIdTypeHandler.cs`, `src/PouryaKit.Persistence.Dapper/DapperServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Converters/TypedIdValueHandler.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Extensions/PersistenceExtensions.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #9
- **Intentional differences:**
  - Generic over the typed ID's value type (Guid, long, int or string, G.3) instead of Guid only; a column of another type is converted to the value type only when `Convert.ChangeType` supports that conversion. It does not parse a string as a Guid.
  - Creates the ID through `TypedId.From`, a constructor looked up once per type, instead of `Activator.CreateInstance` on every row.
  - A null typed ID is passed as `DBNull`.
  - Dapper's type-handler registry is process-wide, and Dapper 2.1.89 serialises registration (`SqlMapper.AddTypeHandlerCore` takes a lock). A handler depends only on its type, so when several Modules or several Hosts in one process register the same typed ID, the replacement behaves identically.
  - Registered by `AddPouryaKitDapper` from the Module's Composition Root, with no generic string-array handler.
