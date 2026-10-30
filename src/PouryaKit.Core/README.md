![PouryaKit.Core](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Core.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Core?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Core)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Core

Small helpers the Kit's own packages use and your code may too. `AndAlso` and `OrElse` compose two expression predicates into one lambda a LINQ provider such as EF Core can translate, `ImplementingTypes.Of` finds the concrete types of given assemblies that implement a contract, and `SynchronizationContextScope` blocks on asynchronous code safely where a synchronous member has to.

## Install

```bash
dotnet add package PouryaKit.Core --prerelease
```

## When to use it

- You build a query filter from optional parts and want one predicate EF Core can translate.
- Your application registers its own implementations of a contract it finds in its assemblies.
- A synchronous member has to wait for asynchronous code on a thread that may have a synchronization context.

You rarely reference it yourself: [`PouryaKit.Cqrs`](https://www.nuget.org/packages/PouryaKit.Cqrs), [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web) and the packages built on them already bring it.

## Quick start

There is nothing to register. From the Kit's tests, a composed predicate, the type lookup and the scope:

```csharp
var composed = AboveTen().AndAlso(Even()).Compile();
Assert.Equal([typeof(Ledger)], ImplementingTypes.Of(typeof(Store<>), Fixtures));
using var scope = SynchronizationContextScope.Enter();
```

## What you get

- One predicate from two, with each parameter rewritten, so the provider translates the whole filter.
- The lookup the Kit's own registrations use to find handlers, authorizers and endpoints.
- A scope that runs a blocking wait without the current synchronization context.

> [!TIP]
> Core stays this small: an architecture test holds its public types to a fixed set. A helper that belongs to one Capability lives in that Capability's package, and one that belongs to your application lives in your application.

## Learn more

- [Core helpers](https://pouryakit.vercel.app/docs/capabilities/core)
- [What Core never holds](https://pouryakit.vercel.app/docs/capabilities/core#what-core-never-holds)
- [All the packages](https://pouryakit.vercel.app/docs/reference/packages)
