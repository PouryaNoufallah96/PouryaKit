![PouryaKit.Reporting.Module](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Reporting.Module.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Reporting.Module?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Reporting.Module)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Reporting.Module

The Kit's report Module, a Module shipped as a package. A Host adds it once beside its own Modules. An Actor requests a report job over HTTP, and the Module stores the job in its own schema, generates the file in the background, stores it in the Host's one file storage provider, and serves the job's state and a time-limited download link to its requester. A recurring retention removes expired jobs and their files.

## Install

```bash
dotnet add package PouryaKit.Reporting.Module --prerelease
```

## When to use it

- An Actor requests a report that takes a while to build, and downloads it when it is ready.
- You add report kinds to a Host by implementing the two ports.
- You need to know what a stale claim, a late settlement, another Actor or tenant, or the retention does to a job.

It renders with [`PouryaKit.Reporting`](https://www.nuget.org/packages/PouryaKit.Reporting) and stores through [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage), so the Host composes one provider, such as [`PouryaKit.Storage.S3`](https://www.nuget.org/packages/PouryaKit.Storage.S3).

## Quick start

Add the Module to the Host and implement its two ports, as the Proving Sample does. The Host also registers an `IActorPermissions` of [`PouryaKit.Security`](https://www.nuget.org/packages/PouryaKit.Security) and one file storage provider, or its build fails:

```csharp
modules.Add<ReportModule>();
builder.Services.AddSingleton<IReportRequestAuthorizer, SalesReports>();
builder.Services.AddSingleton<IReportLayouts, SalesReports>();
```

The first port decides who may request a kind, with which parameters:

```csharp
public Task<bool> MayRequestAsync(
    OperationContext context,
    ReportRequest request,
    CancellationToken cancellationToken
)
{
    return Task.FromResult(
        context.Actor.Kind == ActorKind.Person
            && context.Tenant is not null
            && request.Kind == Kind
            && MonthOf(request) is not null
    );
}
```

## What you get

- A job stored with its requester and tenant.
- Generation by attempts that each claim the job, so a stale claim can be taken over.
- A job served within its tenant to its requester, or an Actor the Host grants the tenant's jobs.
- A retention that removes expired jobs and their files.

> [!WARNING]
> The Module stores over one fixed connection string name and cannot be told another, since a Composition Root takes no parameters. Give the Host's database that name and keep the message store on the same database ([Composing it](https://pouryakit.vercel.app/docs/capabilities/reporting-module#composing-it)).

> [!TIP]
> The file is rendered in memory inside the attempt's Unit of Work, so keep a kind to what fits there.

## Learn more

- [Report Module](https://pouryakit.vercel.app/docs/capabilities/reporting-module)
- [The Host's ports](https://pouryakit.vercel.app/docs/capabilities/reporting-module#the-hosts-ports)
- [A job's life](https://pouryakit.vercel.app/docs/capabilities/reporting-module#a-jobs-life)
- [Reporting](https://pouryakit.vercel.app/docs/capabilities/reporting)
