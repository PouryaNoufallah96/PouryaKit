![PouryaKit.Persistence.EFCore](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Persistence.EFCore.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Persistence.EFCore?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Persistence.EFCore)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Persistence.EFCore

The EF Core Unit of Work. A Module's DbContext derives from `PouryaKitDbContext`, and for each Operation the Unit of Work runs the Domain Event Handlers, stamps audit and soft-delete fields, writes the Outbox, saves once and commits. A concurrent change, or a duplicate value on a unique index the Module mapped, comes back as an Error.

## Install

```bash
dotnet add package PouryaKit.Persistence.EFCore --prerelease
```

## When to use it

- You are mapping an aggregate.
- Two aggregates of one Module must change together, in one commit: a Domain Event Handler.
- An aggregate needs audit fields, soft delete or a unique value.
- You read a sorted and filtered Page through the repository.

You rarely reference it alone: [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres) brings it and adds the database. [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore) joins its Unit of Work to the message store.

## Quick start

Derive the Module's DbContext, then map only what is specific, as the Proving Sample does:

```csharp
internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : PouryaKitDbContext(options);

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public const string NameIndex = "ix_products_name";

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(product => product.Name).HasMaxLength(200);
        builder
            .HasIndex(product => new { product.Tenant, product.Name })
            .IsUnique()
            .HasFilter("is_deleted = false")
            .HasDatabaseName(NameIndex);
    }
}
```

## What you get

- One save and one commit per Operation, in a fixed sequence, with no save in your code.
- Domain Event Handlers that join that same commit.
- Audit and soft-delete fields set by the Unit of Work, never by the domain.
- A concurrent change as a conflict Error, and a mapped unique index as the Error the Module chose.
- A Page through the repository, sorted and filtered only by what the query declared.

> [!WARNING]
> The repository writes only inside a Unit of Work, so an add or a remove anywhere else throws instead of being silently lost.

## Learn more

- [Persistence and the Unit of Work](https://pouryakit.vercel.app/docs/capabilities/persistence)
- [The Unit of Work](https://pouryakit.vercel.app/docs/capabilities/persistence#the-unit-of-work)
- [Versions and conflicts](https://pouryakit.vercel.app/docs/capabilities/persistence#versions-and-conflicts)
- [Dapper or an EF Core Page](https://pouryakit.vercel.app/docs/decision-guides/dapper-or-ef-core-page)
