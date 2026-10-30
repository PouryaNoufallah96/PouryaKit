![PouryaKit.Messaging.Wolverine.Sagas](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Messaging.Wolverine.Sagas.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Messaging.Wolverine.Sagas?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.Sagas)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Messaging.Wolverine.Sagas

Sagas on Wolverine. A Saga is a Module's long-running reaction across Integration Events: they start it and advance it, it keeps its own state between steps, and its timeouts end it. The Kit routes each step, claims it once in its own transaction, keys the saga by the event's tenant and business id, and writes what a step emits into the Module's Outbox.

## Install

```bash
dotnet add package PouryaKit.Messaging.Wolverine.Sagas --prerelease
```

## When to use it

- A reaction spans several Integration Events and must remember what it saw between them.
- Waiting has a deadline: a timeout ends the process when an event never arrives.
- Several payments, approvals or replies must all arrive before one outcome is published.

It builds on [`PouryaKit.Messaging.Wolverine`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine) and keeps its state in the PostgreSQL message store of [`PouryaKit.Messaging.Wolverine.EFCore`](https://www.nuget.org/packages/PouryaKit.Messaging.Wolverine.EFCore).

## Quick start

Declare the saga, with its system Actor, in the Composition Root, as the Kit's tests do:

```csharp
module.AddPouryaKitSaga<FulfilmentSaga>(
    Fulfilment,
    systemActor: Fulfilment,
    saga => saga
        .Input<OrderPlacedV1>(placed => placed.OrderId)
        .Input<PaymentReceivedV1>(paid => paid.OrderId)
        .Input<PaymentReceivedV2>(paid => paid.OrderId)
        .Timeout<FulfilmentTimeout>()
);
```

An Integration Event starts it and schedules its timeout, and the timeout ends it:

```csharp
public static (FulfilmentSaga, FulfilmentTimeout) Start(SagaInput<FulfilmentSaga, OrderPlacedV1> placed) =>
    (
        new FulfilmentSaga { Order = placed.Event.OrderId, Installments = placed.Event.Installments },
        new FulfilmentTimeout(TimeSpan.FromSeconds(placed.Event.PayWithinSeconds))
    );

public async Task Handle(FulfilmentTimeout timeout)
{
    await PublishAsync(new OrderCancelledV1(Order, Paid));
    MarkCompleted();
}
```

## What you get

- State kept between steps, on Wolverine's lightweight saga storage in the message store.
- Each step claimed once, in its own transaction.
- A saga keyed by the event's tenant and business id, so two tenants never share one.
- Emitted Integration Events written to the Module's Outbox under the saga's system Actor.

> [!WARNING]
> A step runs outside every Operation: no authorizer, validator or Unit of Work runs for it, and a step that dies before its commit runs again. So a step only changes the saga's state, emits and schedules timeouts. A call to another service belongs to a subscription that receives what the saga emits.

## Learn more

- [Sagas](https://pouryakit.vercel.app/docs/capabilities/sagas)
- [Saga](https://pouryakit.vercel.app/docs/concepts/saga)
- [What a step must not do](https://pouryakit.vercel.app/docs/capabilities/sagas#what-a-step-must-not-do)
- [The Host](https://pouryakit.vercel.app/docs/capabilities/sagas#the-host)
