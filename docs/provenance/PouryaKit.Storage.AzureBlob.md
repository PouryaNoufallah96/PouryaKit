# PouryaKit.Storage.AzureBlob provenance

Adopted implementations in `PouryaKit.Storage.AzureBlob`. Format and rules: [README](README.md).

The provider, `src/PouryaKit.Storage.AzureBlob/AzureBlobFileStorage.cs`, implements the contract recorded in [PouryaKit.Storage](PouryaKit.Storage.md) with the Azure SDK's standard client calls (`UploadAsync`, `DownloadStreamingAsync`, `DeleteIfExistsAsync`, `GenerateSasUri`), and its options validation follows the Kit's own options pattern, validated when the Host starts, as `PouryaKit.Storage.S3` does (ticket #128). It registers through `AddPouryaKitFileStorage` of `PouryaKit.Storage`, which holds the one-provider composition check.

## Package dependencies

Checked on nuget.org on 2026-10-18 (ticket #128); licence expressions from each package's restored nuspec. None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `Azure.Storage.Blobs` 12.30.1, `MIT`, the latest stable, with a `net10.0` asset; plan I.3 named 12.30.0, and this is its patch release. It brings `Azure.Storage.Common` 12.29.0, `Azure.Core` 1.60.0, `System.IO.Hashing` 10.0.9, `System.ClientModel` 1.14.0, `System.Memory.Data` 10.0.9 and `Microsoft.Bcl.AsyncInterfaces` 10.0.9, all `MIT`; through `Azure.Core`, `Microsoft.Identity.Client` and `Microsoft.Identity.Client.Extensions.Msal` 4.84.2 and `Microsoft.IdentityModel.Abstractions` 8.14.0, all `MIT`, though nothing here signs in with Microsoft Entra; and the two packages below at versions with no licence expression. No `Azure.Identity` arrives.
- `Microsoft.Extensions.Options` 10.0.12, `MIT`, the Kit's line of `Microsoft.Extensions.*`, referenced under the standing rule of plan section C for the options the Host binds and the Kit validates at start.
- `Apache.Arrow` 23.0.0, `Apache-2.0`: a direct reference that raises the 22.1.0 `Azure.Storage.Blobs` asks for, whose nuspec gives a licence file (the Apache 2.0 text) and no expression, which `LicenceTests` refuses. Nothing in the provider calls it: Blobs uses it for query results in Arrow format.
- `System.Security.Cryptography.ProtectedData` 10.0.12, `MIT`: a direct reference that raises the 4.5.0 `Microsoft.Identity.Client.Extensions.Msal` asks for, whose nuspec gives only a licence URL.

Both floors are references outside plan section C's standing rule, approved by the session for #128 on 2026-10-18 as #124's `System.Security.Cryptography.Pkcs` floor was, for the owner to confirm. Delete each once an `Azure.Storage.Blobs` release depends on a version that declares an expression. `PouryaKit.Storage.AzureBlob` also references `PouryaKit.Storage`. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Test dependencies

- `Testcontainers.Azurite` 4.15.0, `MIT`, checked on nuget.org on 2026-10-18, the Kit's Testcontainers line (4.16.0 is the latest), referenced only by `tests/PouryaKit.Storage.Tests`: the Azurite emulator of `AzureBlobStorageTests`, run from the `mcr.microsoft.com/azure-storage/azurite:3.37.0` image (`MIT`), the latest tag on 2026-10-18, with `--skipApiVersionCheck`: the SDK sends the service version 2026-10-06, which neither the module's default tag, 3.28.0, nor 3.37.0 accepts. It brings only `Testcontainers` 4.15.0. The image is test infrastructure the tests run, not a package anything references; ADR 0002 governs package dependencies.

## Azure Blob registration

- **Kit files:** `src/PouryaKit.Storage.AzureBlob/PouryaKitAzureBlobStorageOptions.cs`, `src/PouryaKit.Storage.AzureBlob/AzureBlobStorageServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/FileUpload/Extensions/AzureStorageExtensions.cs` (lines 22 to 40: one `BlobServiceClient` from a connection string or a service URL)
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #128
- **Intentional differences:**
  - Kept: one client per Host, reaching the account either through a full connection string or through a service URL.
  - The service URL form signs with the account key (`StorageSharedKeyCredential`), where upstream uses `DefaultAzureCredential`: the Kit takes no `Azure.Identity`, and a presigned link needs a key to sign with.
  - The form is chosen by named options validated at start, `ConnectionString` or `ServiceUrl` with `AccountName` and `AccountKey`, where upstream guesses it by parsing one configuration string as a URI, and registers nothing when the string is missing.
  - The Kit registers its own `IFileStorage`, not the SDK's `BlobServiceClient`, so Feature code never names the SDK.
