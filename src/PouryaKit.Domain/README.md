![PouryaKit.Domain](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Domain.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Domain?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Domain)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Domain

The plain types your aggregates are made of. An aggregate derives from `AggregateRoot<TId>` and is keyed by a typed ID, its behaviours check business rules and return a `Result` with a value or an `Error` for an expected business failure, and what happened is raised as a Domain Event. The package has no dependencies, so it adds nothing to your domain but these types.

## Install

```bash
dotnet add package PouryaKit.Domain --prerelease
```

## When to use it

- You are writing an aggregate, an entity or the typed ID that keys it.
- A business rule decides whether a change may happen.
- Something in the domain happened that other code reacts to: a Domain Event.
- You are declaring the Errors of a Business Area.
- Domain code needs the current time.

A Module's handlers return these types through [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs), and [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres) stores its aggregates.

## Quick start

There is nothing to register. A behaviour checks its rule, returns an Error for a business failure and raises a Domain Event on success, as the Proving Sample's `Product` does:

```csharp
public static Result<Product> Create(ProductId id, CategoryId categoryId, string name, string tenant)
{
    ArgumentNullException.ThrowIfNull(id);
    ArgumentNullException.ThrowIfNull(categoryId);
    ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
    if (string.IsNullOrWhiteSpace(name))
    {
        return ProductErrors.NameRequired;
    }

    var product = new Product
    {
        Id = id,
        Tenant = tenant,
        CategoryId = categoryId,
        Name = name.Trim(),
    };
    product.Raise(new ProductCreatedDomainEvent(id, categoryId, product.Name));
    return product;
}
```

## What you get

- `Result` and `Error`: a value on success, one or more named Errors on failure.
- Typed IDs: one sealed record per aggregate, over a `Guid`, `long`, `int` or `string`, so the compiler refuses one aggregate's ID where another's belongs.
- Domain Events raised by the aggregate, for the Unit of Work to run.
- Opt-in markers for audit and soft delete, whose fields the Unit of Work sets, and for a tenant's aggregates.
- `DomainClock`, the time domain code reads, which a test can fix.

> [!WARNING]
> Return an Error for anything an Actor can cause, such as a missing name. Exceptions are for bugs and infrastructure. Never set an aggregate's `Version` or clear its Domain Events: the Unit of Work owns both.

## Learn more

- [Domain primitives](https://pouryakit.vercel.app/docs/capabilities/domain-primitives)
- [Operation, Result and Error](https://pouryakit.vercel.app/docs/concepts/operation-result-error)
- [Domain Event](https://pouryakit.vercel.app/docs/concepts/domain-event)
- [The clock](https://pouryakit.vercel.app/docs/capabilities/domain-primitives#the-clock)
