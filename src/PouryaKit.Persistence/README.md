![PouryaKit.Persistence](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Persistence.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Persistence?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Persistence)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Persistence

The contracts Feature code reaches its data through. A handler loads and adds aggregates with `IRepository<TAggregate, TId>` and returns, and the Unit of Work, which a write model package supplies, keeps everything the Operation changed together or discards it together. `UnitOfWorkErrors` names what a commit can answer, and `IUnitOfWorkConnection` lets a command write a Module-internal table in plain SQL inside that same Unit of Work.

## Install

```bash
dotnet add package PouryaKit.Persistence --prerelease
```

## When to use it

- You are reading and changing an aggregate through the repository and a specification.
- A command writes a projection, a tally or a copy that no aggregate maps.
- You handle a concurrent change as an Error.

A Module references [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres), which brings this package and [`PouryaKit.Persistence.EFCore`](https://www.nuget.org/packages/PouryaKit.Persistence.EFCore). [`PouryaKit.Persistence.Marten`](https://www.nuget.org/packages/PouryaKit.Persistence.Marten) is the other write model, and [`PouryaKit.Persistence.Dapper`](https://www.nuget.org/packages/PouryaKit.Persistence.Dapper) serves reads for display.

## Quick start

There is nothing to register here: the Module's write model fills these contracts. Inject the repository, change the aggregate and return its Result, as the Proving Sample does:

```csharp
internal sealed class RenameProductCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<RenameProductCommand>
{
    public override async Task<Result> Handle(RenameProductCommand command, CancellationToken cancellationToken)
    {
        var product = await products.FindAsync(new ProductId(command.ProductId), cancellationToken);
        return product is null ? ProductErrors.NotFound : product.Rename(command.Name);
    }
}
```

## What you get

- A generic repository with specifications, and nothing on it saves.
- `UnitOfWorkErrors`, so a commit that cannot go through is an Error, not an exception.
- A plain-SQL write that commits or rolls back with the Operation, through `IUnitOfWorkConnection`.

> [!WARNING]
> Nothing in a Module saves or commits. Feature code never calls `SaveChanges` and never opens a transaction, and a structure test rejects a Module that does.

## Learn more

- [Persistence and the Unit of Work](https://pouryakit.vercel.app/docs/capabilities/persistence)
- [The repository](https://pouryakit.vercel.app/docs/capabilities/persistence#the-repository)
- [Unit of Work](https://pouryakit.vercel.app/docs/concepts/unit-of-work)
- [Writing with plain SQL in the Unit of Work](https://pouryakit.vercel.app/docs/capabilities/dapper-reads#writing-with-plain-sql-in-the-unit-of-work)
