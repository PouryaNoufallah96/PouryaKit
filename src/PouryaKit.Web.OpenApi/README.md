![PouryaKit.Web.OpenApi](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Web.OpenApi.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Web.OpenApi?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Web.OpenApi)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Web.OpenApi

API versions a Module declares, and one OpenAPI document for each. The Module declares its versions in its Composition Root, on Asp.Versioning's own version set, and each endpoint serves them on a versioned path and on the unversioned path, where a header selects the version. A deprecated version announces the sunset the Host configures, and the Scalar reference lists every document.

## Install

```bash
dotnet add package PouryaKit.Web.OpenApi --prerelease
```

## When to use it

- A Module's HTTP contract changes and existing clients must keep working.
- An endpoint exists only in some versions of its Module.
- You deprecate a version and announce the date it is meant to end.
- You serve one OpenAPI document per version and the Scalar reference over them.

It extends [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web). A Host that does not register it, and a Module that declares no version, keep the plain unversioned paths.

## Quick start

Declare the Module's versions in its Composition Root, as the Proving Sample's Catalog does:

```csharp
module.AddPouryaKitApiVersions(
    "/catalog",
    versions => versions.HasDeprecatedApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0))
);
```

Register the versions and their documents in the Host, and map the documents:

```csharp
builder.Services.AddPouryaKitOpenApi();
builder.Services.Configure<PouryaKitOpenApiOptions>(builder.Configuration.GetSection("PouryaKit:OpenApi"));
app.MapPouryaKitOpenApi().AllowAnonymous();
```

## What you get

- Versions by URL segment and by header, on one endpoint.
- A deprecated version that announces its configured sunset.
- ASP.NET Core's own OpenAPI document for each version, and no Swagger generator.
- The Scalar API reference over them.

> [!WARNING]
> A version belongs to the Module, but its number is shared: versions of different Modules that share a number share one document and one sunset. A Module that joins a Host declares a version no other Module deprecates.

## Learn more

- [API versions and OpenAPI](https://pouryakit.vercel.app/docs/capabilities/openapi)
- [Declaring a Module's versions](https://pouryakit.vercel.app/docs/capabilities/openapi#declaring-a-modules-versions)
- [Deprecation and sunset](https://pouryakit.vercel.app/docs/capabilities/openapi#deprecation-and-sunset)
- [Add an endpoint](https://pouryakit.vercel.app/docs/guides/add-an-endpoint)
