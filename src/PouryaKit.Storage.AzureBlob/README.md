![PouryaKit.Storage.AzureBlob](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/covers/PouryaKit.Storage.AzureBlob.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Storage.AzureBlob?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Storage.AzureBlob)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Storage.AzureBlob

The Host's file storage on Azure Blob Storage. It supplies `IFileStorage` of [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage) on the Azure SDK, from options the Host validates, so Feature code stays on the contract and never names the SDK.

## Install

```bash
dotnet add package PouryaKit.Storage.AzureBlob --prerelease
```

## When to use it

- The application keeps its files on Azure Blob Storage.
- You test against Azurite.

It implements [`PouryaKit.Storage`](https://www.nuget.org/packages/PouryaKit.Storage). A Host composes one provider, so it replaces [`PouryaKit.Storage.S3`](https://www.nuget.org/packages/PouryaKit.Storage.S3) rather than sitting beside it.

## Quick start

Bind the provider's options from configuration, then register it in the Host, as the Kit's tests do:

```csharp
builder.Services.Configure<PouryaKitAzureBlobStorageOptions>(builder.Configuration.GetSection("PouryaKit:Storage:AzureBlob"));
builder.Services.AddPouryaKitAzureBlobStorage();
```

## What you get

- The contract of `IFileStorage` on the Azure SDK.
- Options validated, so a Host with a wrong setting does not serve.
- Presigned links signed with the account key.

> [!WARNING]
> Presigned links need an account key: a connection string without one cannot sign them, and the options refuse it.

## Learn more

- [Azure Blob Storage](https://pouryakit.vercel.app/docs/capabilities/storage#azure-blob-storage)
- [File storage](https://pouryakit.vercel.app/docs/capabilities/storage)
- [S3 or Azure Blob](https://pouryakit.vercel.app/docs/decision-guides/s3-or-azure-blob)
