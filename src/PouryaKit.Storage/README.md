![PouryaKit.Storage](https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-pub/assets-v1/assets/covers/PouryaKit.Storage.png)

[![NuGet](https://img.shields.io/nuget/vpre/PouryaKit.Storage?style=flat-square&logo=nuget&color=615fff)](https://www.nuget.org/packages/PouryaKit.Storage)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)
![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)

# PouryaKit.Storage

One file storage contract for Feature code. `IFileStorage` uploads a stream under a key, downloads it, deletes it, and gives a presigned link that reads it without credentials until it expires. Every call is cancellable and returns a Result for what a Feature expects, so a missing file is an Error, never an exception, and Feature code never names a cloud SDK. A failure of the store itself throws.

## Install

```bash
dotnet add package PouryaKit.Storage --prerelease
```

## When to use it

- A Feature stores a file, downloads it or deletes it under a key.
- A browser or another system needs a time-limited link to read a file.
- A command records a stored file's key, and a failed command must not leave a record of a missing file.

A provider package supplies the Host's one implementation: [`PouryaKit.Storage.S3`](https://www.nuget.org/packages/PouryaKit.Storage.S3) for S3 and S3-compatible stores, or [`PouryaKit.Storage.AzureBlob`](https://www.nuget.org/packages/PouryaKit.Storage.AzureBlob) for Azure Blob Storage.

## Quick start

Inject `IFileStorage` where a Feature handles a file. From the Kit's tests, an upload and its download:

```csharp
var key = Key();
Assert.True((await fixture.Storage.UploadAsync(key, new MemoryStream(Report), "application/pdf", Cancel)).IsSuccess);

var download = await fixture.Storage.DownloadAsync(key, Cancel);
await using var file = download.Value;
```

## What you get

- One contract on every provider, so Feature code is the same on either.
- A `Result` for each expected outcome, with a missing file as `StorageErrors.FileNotFound`, and an exception for a failure of the store itself.
- Presigned links with a lifetime you choose.
- A Host that fails when it is built unless it composes exactly one provider.

> [!WARNING]
> A file is never part of the commit. Store the file first, under a fresh key, and only then record the key in the command's state. A command that fails after the upload leaves an object no record names, which your application's retention removes.

## Learn more

- [File storage](https://pouryakit.vercel.app/docs/capabilities/storage)
- [Store, then record](https://pouryakit.vercel.app/docs/capabilities/storage#store-then-record)
- [S3 or Azure Blob](https://pouryakit.vercel.app/docs/decision-guides/s3-or-azure-blob)
- [One provider](https://pouryakit.vercel.app/docs/capabilities/storage#one-provider)
