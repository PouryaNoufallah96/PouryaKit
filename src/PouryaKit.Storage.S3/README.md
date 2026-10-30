![PouryaKit.Storage.S3](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Storage.S3.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Storage.S3?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Storage.S3)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Storage.S3

The Host's file storage on Amazon S3 or an S3-compatible store such as MinIO. It supplies `IFileStorage` of [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage) on the AWS SDK, from options the Host validates, so Feature code stays on the contract and never names the SDK.

## Install

```bash
dotnet add package PouryaKit.Storage.S3 --prerelease
```

## When to use it

- The application keeps its files on S3.
- You run an S3-compatible store, such as MinIO or RustFS.

It implements [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage). A Host composes one provider, so it replaces [`PouryaKit.Storage.AzureBlob`](https://www.nuget.org/packages/PouryaKit.Storage.AzureBlob) rather than sitting beside it.

## Quick start

Bind the provider's options from configuration, then register it in the Host, as the Proving Sample does:

```csharp
builder.Services.Configure<PouryaKitS3StorageOptions>(builder.Configuration.GetSection("PouryaKit:Storage:S3"));
builder.Services.AddPouryaKitS3Storage();
```

## What you get

- The contract of `IFileStorage` on the AWS SDK.
- Options validated, so a Host with a wrong setting does not serve.
- Presigned links signed by the SDK.
- S3-compatible stores on the same provider.

> [!TIP]
> A link signed with temporary credentials, such as an instance role's, stops working when they expire, whatever lifetime was asked for.

## Learn more

- [S3](https://pouryakit.vercel.app/docs/capabilities/storage#s3)
- [File storage](https://pouryakit.vercel.app/docs/capabilities/storage)
- [S3 or Azure Blob](https://pouryakit.vercel.app/docs/decision-guides/s3-or-azure-blob)
