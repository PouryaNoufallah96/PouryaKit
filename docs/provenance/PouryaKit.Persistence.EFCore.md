# PouryaKit.Persistence.EFCore provenance

Adopted implementations in `PouryaKit.Persistence.EFCore`. Format and rules: [README](README.md).

The Unit of Work (`EfUnitOfWork`), the generic repository (`EfRepository`), the DbContext base (`PouryaKitDbContext`), the Unit of Work connection (`EfUnitOfWorkConnection`, ticket #9), the record of a DbContext's connection string (`DbContextConnection`, ticket #63) and the composition check of each named one (`DbContextConnectionsCheck`, #62's `PostgresConnectionsCheck` moved here in the Wave 2 final review, 2026-10-08, which reads `IConfiguration` through `Microsoft.Extensions.Hosting.Abstractions` with no reference added) and the composition check that the Unit of Work has an Outbox in a Host that has one (`UnitOfWorkOutboxCheck`, PR #143 review) are new Kit code (plan section B.2, "Unit of work"; ADR 0003; ADR 0004), so they have no record here. The order of the Unit of Work's Domain Event drain is recorded with the drain, in [PouryaKit.Cqrs](PouryaKit.Cqrs.md).

Persistence preparation (`PersistencePreparation`, `PouryaKitModelConvention`) and the conflict Errors (`UnitOfWorkErrors`, in `PouryaKit.Persistence` since ticket #131, `IUniqueConstraintReader`, `MapPouryaKitUniqueConstraint`), ticket #11, are new Kit code too: no upstream code was taken. Plan section B.2, "EF base, audit, soft delete", names the designs they replace. `PouryaNoufallah96/cybernod` at `bae8ac3`, `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Interceptors/AuditableEntityInterceptor.cs`, stamps dates in a `SaveChanges` interceptor and counts only owned references that are added or modified; `mehdihadeli/CloudNativeKit` at `e81d65e`, `src/CloudNativeKit.Core/Persistence/EfCore/Interceptors/SoftDeleteInterceptor.cs`, flips a shadow flag during the save. The Kit stamps and converts as a Unit of Work step before the save, with deletion state in real properties, and counts any change to an owned type or a child entity, a removed one included, as a change of its aggregate. The aggregate version it advances is recorded in [PouryaKit.Domain](PouryaKit.Domain.md).

The Outbox seam of the commit sequence's send step (`IUnitOfWorkOutbox`, called by `EfUnitOfWork`) and the accessor to the Unit of Work's DbContext for Kit packages (`EfUnitOfWorkDbContext`, an extension property until ticket #81), ticket #15, are new Kit code too; `PouryaKit.Messaging.Wolverine.EFCore` implements the seam, and its record holds what was taken from Spike S1. Ticket #18 passes the aggregates the prepare step advanced through the same seam, so the Outbox builds their Snapshots, step 4, from that one set.

The tenant filter (`PouryaKitDbContext.TenantFilter`, added by `PouryaKitModelConvention`, ticket #116) is new Kit code too. The plan's I.3 row cites Ultrabusiness's composed query filters (`a85eacd`, `src/BuildingBlocks/BuildingBlocks.Persistence/Extensions/EntityFrameworkCoreModelBuilderExtensions.cs:33-55`) as an idea only; the Kit uses EF Core 10's named filters, one per concern, and took no code. The prepare step's ownership check (ticket #117), which refuses a save of another tenant's entity by the tenant it was loaded under, is new Kit code too, the plan's own rule (I.3). The refusal of an attached tenant-scoped entity (ticket #152) is new Kit code too, over EF Core's own `ChangeTracker.Tracked` event, and the tenant as a concurrency token beside it, with a part's key to its holder, is EF Core's own `IsConcurrencyToken`, set by the same convention. The part filter (ticket #153), the same named filter on a part of a tenant-scoped aggregate as an `Any` over its holder's `EntityQueryRootExpression`, the root EF Core's own `QueryFilterRewritingConvention` makes of `Set<T>()` in a filter, is new Kit code too.

The tracked-state comparison (`TrackedState`), shared by the EF Unit of Work's reading guard and `SnapshotStep`, is new Kit code too. It compares mapped values recursively through EF's value comparers.

The idempotency key store (`EfIdempotencyStore`, `IdempotencyTable`, ticket #42) is new Kit code too, on the pattern of the Kit's own Receipts in `PouryaKit.Messaging.Wolverine.EFCore`: a table outside the Module's model, written with `insert … on conflict do nothing` on the Unit of Work's transaction. Its [PouryaKit.Cqrs](PouryaKit.Cqrs.md) record names the reference implementations it replaces. Its retention (`PouryaKitIdempotencyOptions`, `IdempotencyKeyCleanup`, #42 follow-up) is new Kit code too, a `BackgroundService` on a `PeriodicTimer`. The default retention of 24 hours follows Stripe's API reference, "Idempotent requests" (https://docs.stripe.com/api/idempotent_requests, checked 2026-10-07): keys may be removed once they are at least 24 hours old, and a key reused after that starts a new request.

## Package dependencies

Checked on nuget.org on 2026-10-05, and on 2026-10-07 for the Hosting abstractions (10.0.12, the latest stable); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Microsoft.EntityFrameworkCore.Relational` 10.0.13, `MIT` (10.0.12 until ticket #167, 2026-10-24, as the next).
- `Microsoft.Extensions.Hosting.Abstractions` 10.0.13, `MIT` (#42 follow-up: the version `Directory.Packages.props` already pins for Modularity), for `BackgroundService`.
- `Ardalis.Specification.EntityFrameworkCore` 9.3.1, `MIT`. It has no `net10.0` target; its `net9.0` target asks for EF Core 9.0.8 or later, so it restores against EF Core 10.0.13. Its specification evaluator is exercised at runtime against PostgreSQL by `PersistenceTests.The_generic_repository_finds_by_id_and_finds_lists_checks_and_counts_by_specification`. Its `RepositoryBase` is not used, because its `AddAsync` saves.

## Typed-ID value converter

- **Kit files:** `src/PouryaKit.Persistence.EFCore/TypedIdConverter.cs`, `src/PouryaKit.Persistence.EFCore/PouryaKitDbContext.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Converters/TypedIdValueConverter.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Persistence/Converters/StronglyTypedIdValueConverterSelector.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #8
- **Intentional differences:**
  - Registered once per DbContext in `ConfigureConventions` for every typed ID in the DbContext's assembly, instead of replacing EF Core's internal `IValueConverterSelector` service (plan section B.2).
  - Generic over the typed ID's value type (Guid, long, int or string, G.3) instead of Guid only.
  - Creates the ID through `TypedId.From`, a constructor looked up once per type, instead of reflection on every read.
  - A key with a value converter is never database-generated by convention: a numeric typed ID the database generates needs `ValueGeneratedOnAdd()` in its mapping.
