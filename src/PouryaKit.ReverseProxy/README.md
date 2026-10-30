![PouryaKit.ReverseProxy](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.ReverseProxy.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.ReverseProxy?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.ReverseProxy)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.ReverseProxy

A Reverse proxy on YARP: an application of its own that sits in front of the system's Hosts. It forwards each request by the routes and clusters of its configuration to a Host that service discovery resolves, probes each Host's liveness path and takes a dead endpoint out of its cluster, and keeps the client's trace. It runs no Operation and references no Kit package.

## Install

```bash
dotnet add package PouryaKit.ReverseProxy --prerelease
```

## When to use it

- Several Hosts should answer clients on one address.
- A Module split out as a service keeps its paths, routed to its own Host.
- You want one trace from the client through the proxy to the Operation.

It sits in front of the Hosts of [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web), each of which authenticates its own clients, and [`PouryaKit.Aspire`](https://www.nuget.org/packages/PouryaKit.Aspire) runs it locally.

## Quick start

Compose the proxy as its own application, as the templates generate it:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddPouryaKitReverseProxy();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapReverseProxy();
await app.RunAsync();
```

## What you get

- Routes and clusters from configuration, which change with it at run time.
- Destinations resolved by service discovery.
- A liveness probe that takes a dead endpoint out of its cluster.
- One trace from the client through the proxy to the Operation.

> [!WARNING]
> Liveness only, and no gRPC: the proxy probes no readiness path, since readiness outside Development is the platform's probe, and as shipped it carries no gRPC, so a Host's gRPC client calls the other Host directly.

> [!TIP]
> The proxy authenticates no one: each Host it fronts authenticates its own clients.

## Learn more

- [Reverse proxy](https://pouryakit.vercel.app/docs/capabilities/reverse-proxy)
- [Routes, clusters and discovery](https://pouryakit.vercel.app/docs/capabilities/reverse-proxy#routes-clusters-and-discovery)
- [Limits](https://pouryakit.vercel.app/docs/capabilities/reverse-proxy#limits)
- [Host recipes](https://pouryakit.vercel.app/docs/guides/host-recipes)
