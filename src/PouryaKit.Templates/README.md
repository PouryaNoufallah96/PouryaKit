![PouryaKit.Templates](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Templates.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Templates?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Templates)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Templates

A `dotnet new` template package. A new application starts from the Proving Sample's business code in one of its compositions: the monolith, one Module that grows into the modular monolith when a second Module arrives; the modular monolith, several Modules in one Host; and the service, one Module in a Host of its own that receives another Host's Integration Events and Snapshots over RabbitMQ.

## Install

```bash
dotnet new install PouryaKit.Templates@<version>
```

Name the version: the template package's version becomes the Kit version every generated project pins. [Install the templates](https://pouryakit.vercel.app/docs/get-started/install-templates) gives the published one.

## When to use it

- You start a new application on the Kit.
- You choose between a monolith, a modular monolith and a service.
- You grow the monolith into a modular monolith, or split a Module out as a service over RabbitMQ.

A generated solution references the Kit's packages, such as [`PouryaKit.Modularity`](https://www.nuget.org/packages/PouryaKit.Modularity) and [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web), at the template package's version, and tests itself with [`PouryaKit.Testing`](https://www.nuget.org/packages/PouryaKit.Testing).

## Quick start

Generate one solution, by the composition you want:

```bash
dotnet new pouryakit-monolith -n Acme.Shop
dotnet new pouryakit-modular-monolith -n Acme.Shop
dotnet new pouryakit-service -n Acme.Stock
```

Then start PostgreSQL, apply the migrations and run it: [Your first Host](https://pouryakit.vercel.app/docs/get-started/first-host) walks through each step.

## What you get

- The Proving Sample's own business code, generated rather than maintained beside it.
- A Composition Root and endpoint adapters for each composition.
- A smoke test in every generated solution.
- The SDK, the test runner and `dotnet-ef` pinned as the Kit pins them.

> [!WARNING]
> The generated Host applies no migration at startup and does not start without its bearer settings. [Your first Host](https://pouryakit.vercel.app/docs/get-started/first-host) walks through both.

## Learn more

- [Project templates](https://pouryakit.vercel.app/docs/capabilities/templates)
- [Install the templates](https://pouryakit.vercel.app/docs/get-started/install-templates)
- [Choose a template](https://pouryakit.vercel.app/docs/templates)
- [Growing the monolith](https://pouryakit.vercel.app/docs/capabilities/templates#growing-the-monolith-into-a-modular-monolith)
- [Prepare a release](https://pouryakit.vercel.app/docs/guides/prepare-a-release)
