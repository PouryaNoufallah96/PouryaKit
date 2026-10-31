![PouryaKit.Cqrs](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Cqrs.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Cqrs?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Cqrs)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Cqrs

Commands and queries that run as Operations through one fixed pipeline. You declare each Feature as a sealed record with exactly one handler and send it. The Kit runs it through logging, the optional authorization hook, optional validation and the handler, and a command's handler runs inside the Unit of Work, which commits once for the whole Operation.

## Install

```bash
dotnet add package PouryaKit.Cqrs --prerelease
```

## When to use it

- You are writing a Feature: a command that changes an aggregate, or a query that answers a read.
- A client may send a command again after a response it never received, and should get the first answer.
- A query returns a list that an Actor sorts, filters and pages.
- One command or query needs a rule of its own about who may run it.

It pairs with [`PouryaKit.Validation`](https://www.nuget.org/packages/PouryaKit.Validation) for input checks, [`PouryaKit.Modularity`](https://www.nuget.org/packages/PouryaKit.Modularity) for a Module's Gateway, [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres) for the Unit of Work and [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web) for endpoints.

## Quick start

Register the Features in the Module's Composition Root:

```csharp
module.AddPouryaKitCqrs();
```

Declare the command and its one handler, as the Proving Sample does:

```csharp
[Idempotent]
internal sealed record CreateProductCommand(Guid ProductId, Guid CategoryId, string Name)
    : Command<Guid>;

internal sealed class CreateProductCommandHandler(
    IRepository<Product, ProductId> products,
    OperationContext context
) : CommandHandler<CreateProductCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(
        CreateProductCommand command,
        CancellationToken cancellationToken
    )
    {
        var product = Product.Create(
            new ProductId(command.ProductId),
            new CategoryId(command.CategoryId),
            command.Name,
            context.Tenant!
        );
        if (product.IsFailure)
        {
            return product.Errors;
        }

        await products.AddAsync(product.Value, cancellationToken);
        return product.Value.Id.Value;
    }
}
```

## What you get

- One fixed pipeline for every command and query, with nothing to add per Feature.
- A `Result` from every handler, so an expected business failure is an Error and never an exception.
- An `OperationContext` that says who and what caused the Operation, wherever it runs.
- `[Idempotent]` on a command, so a repeat of a request answers with the first success.
- A Page declared on a query, sorted and filtered only by what the query allows.
- A Host that refuses its own composition mistakes when it is built, with a composition check that names each one.

> [!WARNING]
> A handler returns a Result and never saves. The Unit of Work owns saving, committing, transactions and the Outbox ([Persistence and the Unit of Work](https://pouryakit.vercel.app/docs/capabilities/persistence)).

## Learn more

- [Commands, queries and validation](https://pouryakit.vercel.app/docs/capabilities/commands-queries-validation)
- [Create a command](https://pouryakit.vercel.app/docs/guides/create-a-command)
- [A paged query](https://pouryakit.vercel.app/docs/guides/paged-query)
- [Request idempotency](https://pouryakit.vercel.app/docs/guides/request-idempotency)
- [Operation, Result and Error](https://pouryakit.vercel.app/docs/concepts/operation-result-error)
