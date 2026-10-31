![PouryaKit.Persistence.Postgres](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Persistence.Postgres.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Persistence.Postgres?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Persistence.Postgres

Each Module's EF Core data in its own PostgreSQL schema. `UsePouryaKitPostgres` gives a Module its DbContext on Npgsql, keeps its migrations history in that schema and makes the DbContext the Module's Unit of Work. Modules may share one connection string, and each keeps its own schema.

## Install

```bash
dotnet add package PouryaKit.Persistence.Postgres --prerelease
```

## When to use it

- You give a Module its database and schema: this is the package a Module references to store its aggregates.
- A unique value should come back as the Error of your choice.
- A number must be gap-free, such as one sequence of invoice numbers per year.
- You are adding a migration.

It brings [`PouryaKit.Persistence`](https://www.nuget.org/packages/PouryaKit.Persistence) and [`PouryaKit.Persistence.EFCore`](https://www.nuget.org/packages/PouryaKit.Persistence.EFCore). Add [`PouryaKit.Persistence.Dapper`](https://www.nuget.org/packages/PouryaKit.Persistence.Dapper) for reads in SQL and [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore) for deliveries.

## Quick start

Declare the Module's store in its Composition Root, as the Proving Sample's Catalog does:

```csharp
module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
module.MapPouryaKitUniqueConstraint<CatalogDbContext>(
    ProductConfiguration.NameIndex,
    ProductErrors.NameInUse
);
```

Number sequences take a registration of their own, and a migration of the Module that calls `CreateNumberSequenceTable` with its schema, or the first number throws:

```csharp
module.AddPouryaKitNumberSequences();
```

A command asks for the next number inside its Unit of Work:

```csharp
var number = await sequences.NextAsync($"stock-count:{year}", cancellationToken);
```

## What you get

- A schema per Module, with its own migrations history, on one or several databases.
- A violated unique index read back as the Error the Module mapped.
- Gap-free numbers per scope key, given back when the Operation rolls back.

> [!WARNING]
> The Host applies no migration at startup. Apply each Module's migrations yourself; in tests, `HostFixture` of [`PouryaKit.Testing`](https://www.nuget.org/packages/PouryaKit.Testing) applies them.

## Learn more

- [PostgreSQL](https://pouryakit.vercel.app/docs/capabilities/persistence#postgresql)
- [Add a migration](https://pouryakit.vercel.app/docs/guides/add-a-migration)
- [Unique values](https://pouryakit.vercel.app/docs/capabilities/persistence#unique-values)
- [Number sequences](https://pouryakit.vercel.app/docs/capabilities/persistence#number-sequences)
