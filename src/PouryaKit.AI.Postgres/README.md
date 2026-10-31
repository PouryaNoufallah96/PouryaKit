![PouryaKit.AI.Postgres](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.AI.Postgres.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.AI.Postgres?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.AI.Postgres)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.AI.Postgres

Text stored by its embedding and searched by meaning with pgvector. A command stores text under a key through `IVectorStore`, in the Module's own Unit of Work, so a rolled-back command stores nothing, and a query finds the nearest passages, always within the Operation's tenant.

## Install

```bash
dotnet add package PouryaKit.AI.Postgres --prerelease
```

## When to use it

- You find stored text again by what it means.
- A model looks passages up through a Gateway tool.
- You embed text inside a command and want it to commit with the command.

It builds on [`PouryaKit.AI`](https://www.nuget.org/packages/PouryaKit.AI), which registers the embedding generator, and [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres), whose Unit of Work it writes through.

## Quick start

Add the vector store to a Module stored with `UsePouryaKitPostgres`, and create its collection in a migration, as the Kit's tests do. The Host registers its embedding generator with `AddPouryaKitEmbeddings`:

```csharp
module.AddPouryaKitVectorStore();
migrationBuilder.CreateVectorCollection(VectorsModule.Passages, VectorsModule.Dimensions);
```

A command stores text under a key, then searches by meaning what it stored:

```csharp
internal sealed class StoreThenSearchPassageCommandHandler(IVectorStore vectors)
    : CommandHandler<StoreThenSearchPassageCommand, IReadOnlyList<VectorMatch>>
{
    public override async Task<Result<IReadOnlyList<VectorMatch>>> Handle(
        StoreThenSearchPassageCommand command,
        CancellationToken cancellationToken
    )
    {
        await vectors.StoreAsync(
            VectorsModule.Passages,
            command.Key,
            command.Text,
            cancellationToken
        );
        return Result.Success(
            await vectors.SearchAsync(VectorsModule.Passages, command.Text, 1, cancellationToken)
        );
    }
}
```

## What you get

- Each row keyed by the Operation's tenant: a call never passes a tenant.
- A store that commits or rolls back with the command.
- In a command, a search that sees what the command stored.

> [!WARNING]
> Storing calls the model inside the Unit of Work's transaction, so a slow model holds the Operation's locks.

## Learn more

- [Embeddings and semantic search](https://pouryakit.vercel.app/docs/capabilities/ai#embeddings-and-semantic-search)
- [AI](https://pouryakit.vercel.app/docs/capabilities/ai)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/ai#limits)
