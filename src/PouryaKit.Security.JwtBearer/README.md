![PouryaKit.Security.JwtBearer](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Security.JwtBearer.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Security.JwtBearer?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Security.JwtBearer)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Security.JwtBearer

JWT bearer authentication for a Host. It sets up ASP.NET Core's bearer scheme so each verified token names the Actor and the tenant of the Operations it causes. The Kit issues no token: the issuer, audiences and signing keys are your identity provider's, in the Host's configuration.

## Install

```bash
dotnet add package PouryaKit.Security.JwtBearer --prerelease
```

## When to use it

- Clients present bearer tokens from an identity provider.
- The Host must turn their verified claims into the Actor and tenant.

It builds on [`PouryaKit.Security`](https://www.nuget.org/packages/PouryaKit.Security), and [`PouryaKit.Web`](https://www.nuget.org/packages/PouryaKit.Web) reads the claims it writes into the Operation Context.

## Quick start

Register it in the Host, naming the claim that holds the tenant, with authorization and the Web pipeline:

```csharp
builder.Services.AddPouryaKitJwtBearer(claims => claims.TenantClaim = TenantClaim);
builder.Services.AddAuthorization();
builder.Services.AddPouryaKitWeb();
```

## What you get

- ASP.NET Core's own bearer scheme, with its token validation.
- An Actor and a tenant taken only from claims the scheme verified.
- Settings from the standard authentication configuration section, as any issuer is configured.

> [!WARNING]
> A Host that registers it does not start without its bearer settings. The [first Host](https://pouryakit.vercel.app/docs/get-started/first-host) walks through them for development.

## Learn more

- [JWT bearer authentication](https://pouryakit.vercel.app/docs/capabilities/security#jwt-bearer-authentication)
- [Testing JWT bearer authentication](https://pouryakit.vercel.app/docs/capabilities/security#testing-jwt-bearer-authentication)
- [Your first Host](https://pouryakit.vercel.app/docs/get-started/first-host)
- [Operation Context](https://pouryakit.vercel.app/docs/concepts/operation-context)
