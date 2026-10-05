---
name: persistence-ef-facts
description: Verified EF Core 10 / Npgsql / dotnet-ef / Ardalis facts and the Module Unit of Work routing design from ticket #8
metadata:
  type: project
---

Verified in ticket #8 (2026-10-05), EF Core 10.0.12, Npgsql EF 10.0.3, EFCore.NamingConventions 10.0.1:

- **Unit of Work routing (one container, many Modules):** `ModuleBuilder` implements `IServiceCollection` and re-keys any unkeyed `IUnitOfWork` descriptor by the Module type; `AddPouryaKitModules` then adds one unkeyed router that reads the scope's `ModuleTag` (set by the Gateway). `EfRepository` takes `IUnitOfWork` and uses its DbContext. Persistence must `Add` (not `TryAdd`) its `IUnitOfWork`: a second `AddPouryaKitModules` call sees the first call's unkeyed router and TryAdd silently skips.
- **Allowed-references trap:** Postgres may not reference Modularity, so `module.UsePostgres<T>()` only works because ModuleBuilder *is* an IServiceCollection.
- Default schema comes from `RelationalOptionsExtension.Extract(options).MigrationsHistoryTableSchema` in `PouryaKitDbContext.OnModelCreating`.
- `IgnoreAny<DomainEvent>()` in ConfigureConventions stops `AggregateRoot.DomainEvents` becoming a navigation.
- A converted (typed-ID) numeric key is never made identity by convention; it needs `ValueGeneratedOnAdd()` per entity. With it, EF inserts with identity and reads the id back through the converter; the instance's Id stays null until save (no 0 or temporary value constructed).
- EFCore.NamingConventions snake-cases the history table's columns (`migration_id`) but not its name (`__EFMigrationsHistory`).
- `UseNpgsql("Name=ConnectionStrings:x")` resolves from DI `IConfiguration`; a design-time factory must register one or dotnet-ef fails.
- Ardalis.Specification.EntityFrameworkCore 9.3.1 restores and evaluates fine on EF Core 10; its `RepositoryBase.AddAsync` calls SaveChanges, so use only `SpecificationEvaluator.Default.GetQuery`.
- dotnet-ef 10.0.12: `--namespace` puts the model snapshot in a folder named after the namespace; pass `--output-dir` only. A test-project migration was generated with a temporary `IDesignTimeDbContextFactory` + temporary Design package, both removed after.
- `dotnet new tool-manifest` on SDK 10.0.401 writes `dotnet-tools.json` at the repo root; move it to `.config/`.
- `HostFixture` migrates from a scope of the built, not yet started Host, then starts it (review F2): a hosted service (Wolverine, #15) must find its tables at start. The Proving Sample's own packages are recorded in `docs/provenance/ModularMonolith.md`.
- #12: `EfRepository<,>` is public and unsealed; a specific repository derives from it and builds on `protected ListAsync<TResult>(ISpecification<TAggregate, TResult>)`, which materialises. Review F1 (high): never hand a derived class an `IQueryable` — an empty Ardalis spec returns the DbSet itself (cast → `Entry(x).Context.SaveChanges`), and any EF query allows `ExecuteDelete/ExecuteUpdate`, which write around the Unit of Work's sequence. Ardalis's projection `GetQuery<T,TResult>` has no criteria-only flag (full spec, then Select) and no Distinct. Registered with `module.AddScoped<IProductRepository, ProductRepository>()`; ModuleBuilder re-keys only `IUnitOfWork`, so both repositories share the scope's DbContext.
- Test technique (one container): a command/handler in the test assembly is registered by `StubModule`'s `AddPouryaKitCqrs` scan yet can be sent through `Gateway<CatalogModule>()` and runs in Catalog's Unit of Work. Used by `Stub/ProductRepositoriesProbe.cs`.
- After #10 a Product needs an existing Category (`CountProductInCategoryHandler` fails NotFound otherwise): any Catalog test creating products sends `CreateCategoryCommand` first. Parallel tickets that touch the sample model break each other's branches; expect a post-merge repair round.
- Avoid sample Features premised on duplicate product names: #11 is expected to put a unique constraint on `products.name`. Avoid row locks (`FOR UPDATE`) as an example: a second concurrency mechanism beside versions.
- `tests/PouryaKit.Cqrs.Tests` holds deliberately invalid operations: never `AddPouryaKitCqrs(thatAssembly)`; register test handlers by hand.

**Why:** each cost a run or a design detour; #9-#16 build on these seams.
**How to apply:** start from these when touching Persistence.*, Modularity or Testing; re-verify when bumping EF/Npgsql. Related: [[cqrs-design-facts]], [[dotnet-test-conventions]].
