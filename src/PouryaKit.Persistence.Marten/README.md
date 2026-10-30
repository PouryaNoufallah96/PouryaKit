![PouryaKit.Persistence.Marten](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Persistence.Marten.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Persistence.Marten?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Persistence.Marten)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Persistence.Marten

Streams on Marten over PostgreSQL: the Kit's second write model. A Stream is an aggregate whose stored state is the events it appended. A behaviour applies its event to the aggregate's state and raises it, the Unit of Work appends what was raised, and loading the Stream folds its stored events through the same `Apply` methods.

## Install

```bash
dotnet add package PouryaKit.Persistence.Marten --prerelease
```

## When to use it

- An aggregate's history is the point: a ledger, an account, an audit of every change.
- You register a Stream Module, beside EF Core Modules or without them.
- You load and start Streams in a handler, and handle a concurrent append as a conflict.

A Host with a message store adds [`PouryaKit.Messaging.Wolverine.Marten`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.Marten), which commits a Stream's events with what it sends. [`PouryaKit.Persistence.Postgres`](https://www.nuget.org/packages/PouryaKit.Persistence.Postgres) is the other write model.

## Quick start

Declare the write model in the Module's Composition Root:

```csharp
module.AddPouryaKitStreams(schema: "ledger");
```

Write behaviour as events, as the Proving Sample's Ledger does:

```csharp
internal sealed class Account : StreamAggregate
{
    public string Holder { get; private set; } = "";

    public decimal Balance { get; private set; }

    public void Post(decimal amount)
    {
        var posted = new EntryPostedDomainEvent(new AccountId(Id), amount);
        Apply(posted);
        Raise(posted);
    }

    public void Apply(EntryPostedDomainEvent posted) => Balance += posted.Amount;
}
```

Load and change a Stream in the handler through `IStreams`:

```csharp
internal sealed class PostEntryCommandHandler(IStreams streams) : CommandHandler<PostEntryCommand>
{
    public override async Task<Result> Handle(PostEntryCommand command, CancellationToken cancellationToken)
    {
        if (await streams.LoadAsync<Account>(new AccountId(command.AccountId), cancellationToken) is not { } account)
        {
            return AccountErrors.NotFound;
        }

        account.Post(command.Amount);
        return Result.Success();
    }
}
```

## What you get

- A Stream Module that is still a Module: the same Gateway, pipeline and Notification Handlers.
- One commit owner per write model, so each Operation commits through exactly one.
- A concurrent append refused as a conflict Error, with nothing appended.
- A Stream that belongs to the tenant of the Operation that wrote it.

> [!WARNING]
> A Stream publishes no Snapshot and has no plain-SQL connection, realtime push or number sequence, and it cannot share one Host with a Saga yet.

## Learn more

- [Streams](https://pouryakit.vercel.app/docs/capabilities/stream)
- [Stream](https://pouryakit.vercel.app/docs/concepts/stream)
- [Stream or EF Core aggregate](https://pouryakit.vercel.app/docs/decision-guides/stream-or-ef-core-aggregate)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/stream#limits)
