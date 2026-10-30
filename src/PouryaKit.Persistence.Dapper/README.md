![PouryaKit.Persistence.Dapper](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Persistence.Dapper.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Persistence.Dapper?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Persistence.Dapper)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Persistence.Dapper

Plain-SQL reads with Dapper on a pooled PostgreSQL connection. A query's handler takes `IReadConnectionFactory`, leases an open connection from the Host's one pool, and every typed ID of the Module works as a Dapper parameter and result. For a list, `PageSql` and `QueryPageAsync` read one Page of it, sorted and filtered only by what the query declared.

## Install

```bash
dotnet add package PouryaKit.Persistence.Dapper --prerelease
```

## When to use it

- A query reads rows for display, by ID or by a filter you write.
- A query answers a sorted and filtered Page of a list.
- You read or write typed IDs with Dapper.

It pairs with [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs), whose queries it serves, and sits beside [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres), whose EF Core model writes what you read here.

## Quick start

Register the read connection in the Composition Root:

```csharp
module.AddPouryaKitDapper(connectionName: "app");
```

Lease a connection in the handler and write the SQL against your own schema, as the Proving Sample does:

```csharp
internal sealed class GetProductQueryHandler(IReadConnectionFactory connections)
    : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var product = await connection.QuerySingleOrDefaultAsync<ProductDto>(
            new CommandDefinition(
                "select id, category_id as categoryid, name from catalog.products where id = @Id and tenant = current_setting('pouryakit.tenant') and is_deleted = false",
                new { Id = new ProductId(query.ProductId) },
                cancellationToken: cancellationToken
            )
        );
        return product is null ? ProductErrors.NotFound : product;
    }
}
```

## What you get

- One PostgreSQL pool per Host, leased to each read.
- Typed IDs as parameters and results, with nothing to register for each one.
- A Page whose sort and filters come only from what the query declared, so input never becomes SQL.
- In a tenant-scoped Module, a lease that binds the Operation's tenant.

> [!WARNING]
> Dapper runs exactly the SQL you write, so no EF Core query filter reaches it. A read of a soft-deletable table leaves out the removed rows itself, and in a tenant-scoped Module a read of a tenant-scoped table states the tenant the lease bound.

## Learn more

- [Dapper reads](https://pouryakit.vercel.app/docs/capabilities/dapper-reads)
- [Reading a Page](https://pouryakit.vercel.app/docs/capabilities/dapper-reads#reading-a-page)
- [A paged query](https://pouryakit.vercel.app/docs/guides/paged-query)
- [Dapper or an EF Core Page](https://pouryakit.vercel.app/docs/decision-guides/dapper-or-ef-core-page)
