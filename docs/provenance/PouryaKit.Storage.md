# PouryaKit.Storage provenance

Adopted implementations in `PouryaKit.Storage`. Format and rules: [README](README.md).

## Package dependencies

None of its own. The package references `PouryaKit.Domain`, for `Result` and `Error`, as plan section C lists since ticket #127, and `PouryaKit.Cqrs`, since ticket #128, for the one-provider composition check (`src/PouryaKit.Storage/OneFileStorageCheck.cs`, the Kit's own `ICompositionCheck` shape, moved from `PouryaKit.Storage.S3`) that `src/PouryaKit.Storage/FileStorageServiceCollectionExtensions.cs` registers for every provider. Its closure holds Cqrs's Microsoft.Extensions packages, and `PackageGraphTests` keeps every AWS and Azure package out of it, so a provider's SDK stays in the provider's package. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## File storage contract

- **Kit files:** `src/PouryaKit.Storage/IFileStorage.cs`, `src/PouryaKit.Storage/StoredFile.cs`, `src/PouryaKit.Storage/StorageErrors.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd4db5e1af9b8349d49350be12397f24402
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.FileManagement/Contracts/IFileStorageProvider.cs` (lines 18 to 59: the provider contract)
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #127
- **Intentional differences:**
  - Four calls, as spec #115 names them: upload, download, delete and a presigned link. Upstream's `ExistsAsync` and `GetMetadataAsync` are dropped: a Feature records what it stores.
  - Every call takes a required `CancellationToken` and returns a `Result`: an upload and a delete a plain `Result`, a download a `Result<StoredFile>`, a link a `Result<Uri>`. Upstream returns its own `FileStorageResult` and `FileDownloadResult`, a `bool` and a nullable `Uri`, and its tokens default.
  - The caller chooses the key and passes the stream and content type directly, where upstream takes a `FileUploadRequest` and returns the key it stored under; the store-then-record pattern needs the key before the upload.
  - A missing file is one Error, `StorageErrors.FileNotFound`, of `ErrorKind.NotFound`; a link is never null, since every provider of the Kit can sign one.
