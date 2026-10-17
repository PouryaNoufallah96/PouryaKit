# PouryaKit.Storage.S3 provenance

Adopted implementations in `PouryaKit.Storage.S3`. Format and rules: [README](README.md).

The package has no adopted implementation. The provider, `src/PouryaKit.Storage.S3/S3FileStorage.cs`, implements the contract recorded in [PouryaKit.Storage](PouryaKit.Storage.md) with the AWS SDK for .NET's standard client calls (`PutObjectAsync`, `GetObjectAsync`, `DeleteObjectAsync`, `GetPreSignedURLAsync`), and its registration, `src/PouryaKit.Storage.S3/S3StorageServiceCollectionExtensions.cs`, follows the Kit's own options pattern, validated when the Host starts (ticket #127).

## Package dependencies

Checked on nuget.org on 2026-10-17 (ticket #127); licence expressions from each package's restored nuspec. None is reciprocal or commercial (ADR 0002), and `LicenceTests` checks the whole closure.

- `AWSSDK.S3` 4.0.104.4, `Apache-2.0`, the latest stable, with a `net8.0` asset; spec #115 verified 4.0.104.2 on 2026-10-12, and this is its patch release of 2026-10-14. It brings `AWSSDK.Core` 4.0.102.11, `Apache-2.0`, and nothing else for `net8.0` and later.
- `Microsoft.Extensions.Options` 10.0.12, `MIT`, for the options the Host binds and their validation at start. It brings `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Primitives` 10.0.12, both `MIT`.

`PouryaKit.Storage.S3` also references `PouryaKit.Storage`, as plan section C lists since ticket #127. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Test dependencies

- `Testcontainers` 4.15.0, `MIT`, checked on nuget.org on 2026-10-17, the Kit's Testcontainers line (4.16.0 is the latest), referenced only by `tests/PouryaKit.Storage.Tests`: its generic `ContainerBuilder` runs the RustFS store of `S3StorageTests` from the `rustfs/rustfs:1.0.2` image (`Apache-2.0`, released 2026-10-16, public on Docker Hub), which has no Testcontainers module. It replaced `Testcontainers.Minio` and the `minio/minio` image in ticket #148, after MinIO withdrew its images from Docker Hub and the GitHub runner could no longer pull them. The image is test infrastructure the tests run, not a package anything references; ADR 0002 governs package dependencies.
