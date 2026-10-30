![PouryaKit.Modularity](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Modularity.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Modularity?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Modularity)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Modularity

Modules for a Host. A Module is a business boundary that declares what it needs and offers in one public class, its Composition Root, and keeps everything else internal. Anything outside it, an endpoint, a job, another Module or a test, runs one of its Operations through its Gateway and passes an Operation Context that says which Actor it runs for.

## Install

```bash
dotnet add package PouryaKit.Modularity --prerelease
```

## When to use it

- You are writing a Module's Composition Root, or composing the Host from its Modules.
- Code outside a Module needs to run one of its commands or queries.
- One Module needs an answer from another before its Operation can go on.
- A Host fails when it is built or started, and you want to know which check stopped it.

It builds on [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs). A Host with a single Module may leave it out, as the [monolith template](https://pouryakit.vercel.app/docs/templates/monolith) does until a second Module arrives.

## Quick start

Add each Module to the Host:

```csharp
builder.AddPouryaKitModules(modules => modules.Add<InventoryModule>());
```

Declare the Module in its Composition Root, as the Proving Sample's Billing does:

```csharp
public sealed class BillingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<BillingDbContext>(connectionName: "app", schema: "billing");
        module.AddPouryaKitCqrs();
        module.Subscribe<ProductCreatedV1, OpenPriceListIntegrationEventHandler>("billing.product-created");
        module.Subscribe<AccountOpenedV1, OpenWelcomeQuoteIntegrationEventHandler>("billing.account-opened");
        module.AddPouryaKitDelayedCommand<ExpireQuoteCommand>("billing.expire-quote");
    }
}
```

Run an Operation through its Gateway, with the context of the running request:

```csharp
routes.MapGet(
    "/stock-items/{productId}",
    async (Guid productId, OperationContext context, IGatewayProcessor<InventoryModule> inventory, CancellationToken cancellationToken) =>
        (await inventory.QueryAsync(new GetStockItemQuery(productId), context, cancellationToken)).ToHttpResult()
);
```

## What you get

- One public class per Module, with everything else `internal`, and architecture tests that hold the boundary.
- `IGatewayProcessor<TModule>`, the only way in: each call is one Operation in a scope of its own.
- Every Operation belongs to one Module, which owns what it declares.
- A Host that fails when it is built or started, naming what its composition got wrong, before it serves.

> [!WARNING]
> There is no anonymous or default Actor. Every Gateway call needs an Operation Context: create a new one for each Operation that starts work, with `Actor.Person` or a named `Actor.System`, and pass `Continue()` of the running context for work an Operation causes.

## Learn more

- [Modules](https://pouryakit.vercel.app/docs/capabilities/modules)
- [Host and Module](https://pouryakit.vercel.app/docs/concepts/host-and-module)
- [Add a Module](https://pouryakit.vercel.app/docs/get-started/add-a-module)
- [The Host's checks](https://pouryakit.vercel.app/docs/capabilities/modules#the-hosts-checks)
