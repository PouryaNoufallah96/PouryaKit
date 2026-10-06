# PouryaKit.Persistence provenance

Adopted implementations in `PouryaKit.Persistence`. Format and rules: [README](README.md).

The generic repository contract, `IRepository<TAggregate, TId>`, is new Kit code (G.4), so it has no record here. Neither has the Unit of Work connection seam, `IUnitOfWorkConnection` (ticket #9): Cybernod's Dapper reads never join the Unit of Work's transaction (plan section B.2, "Dapper reads"), so it is new Kit code.

## Package dependencies

`Ardalis.Specification` 9.3.1, licence `MIT` as published on nuget.org (licence expression in the package's nuspec; no dependencies on `net8.0` and `net9.0`; checked 2026-10-05). It is the package's only third-party reference (G.4).
