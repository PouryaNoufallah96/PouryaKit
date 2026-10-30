![PouryaKit.Localization](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Localization.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Localization?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Localization)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Localization

A Module's messages in English and Persian. A Feature injects `IStringLocalizer<T>` for a type of its own and reads each message by its key, from `.resx` files beside that type's file. Each HTTP request runs in English or Persian as its client's `Accept-Language` header names, in English otherwise, and that culture flows through the Gateway into the Operation's handlers.

## Install

```bash
dotnet add package PouryaKit.Localization --prerelease
```

## When to use it

- A Module's messages must read in English and Persian.
- You read the request culture from the client's language header.
- A cached query must answer each culture in its own language.

It is optional and references only [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs) and the ASP.NET Core shared framework. A Host that answers in English alone leaves it out.

## Quick start

Register it in the Composition Root of each Module that writes localized messages:

```csharp
module.AddPouryaKitLocalization();
```

Put the messages beside a marker type, and read each one by its key, as the Kit's tests do:

```csharp
internal sealed class StubMessages;

internal sealed record GreetQuery : Query<string>;

internal sealed class GreetQueryHandler(IStringLocalizer<StubMessages> messages) : QueryHandler<GreetQuery, string>
{
    public override Task<Result<string>> Handle(GreetQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(messages["Greeting"].Value);
}
```

## What you get

- The shared framework's `IStringLocalizer<T>`, with no extra NuGet package.
- The request culture applied ahead of the Host's own middleware, with nothing to call in its pipeline.
- The culture carried through the Gateway into the Operation's handlers.

> [!WARNING]
> A delivery runs in the process's culture. A Notification Handler, an Integration Event Handler or a delayed or recurring command runs outside any HTTP request, so it does not read the culture of the request that caused it.

## Learn more

- [Localization](https://pouryakit.vercel.app/docs/capabilities/localization)
- [A Module writing localized messages](https://pouryakit.vercel.app/docs/capabilities/localization#a-module-writing-localized-messages)
- [What the Kit guarantees](https://pouryakit.vercel.app/docs/capabilities/localization#what-the-kit-guarantees)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/localization#limits)
