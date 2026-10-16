---
name: storage-and-s3-facts
description: AWS SDK v4 against MinIO (path style, signing region, presign protocol, NoSuchKeyException), Testcontainers.Minio, and what adding a Kit package moves in the diagrams
metadata:
  type: project
---

Check the merged code before relying on a design note. Each section names its ticket, its date and the versions it was verified on.

## The S3 provider against MinIO (#127, 2026-10-17; AWSSDK.S3 4.0.104.4, AWSSDK.Core 4.0.102.11, Testcontainers.Minio 4.15.0)

- **MinIO needs `ServiceURL`, `ForcePathStyle = true` and `AuthenticationRegion`** (any, `us-east-1`) on `AmazonS3Config`; with no service URL set `RegionEndpoint` instead. The SDK's default checksums (v4 sends them on `PutObject`) worked against `minio/minio:RELEASE.2025-04-22T22-12-26Z` (cached locally; Testcontainers.Minio's default image is from 2023): no `WHEN_REQUIRED` setting needed.
- **A presigned link is signed for HTTPS whatever the endpoint** (`GetPreSignedUrlRequest.Protocol` defaults to HTTPS): against an HTTP MinIO the fetch fails with "The SSL connection could not be established". Set `Protocol.HTTP` when the service URL is HTTP. `GetPreSignedURLAsync(request)` takes no token. A one-second link was refused with 403 after three seconds (no clock-skew trouble with Docker Desktop).
- **A missing key throws `Amazon.S3.Model.NoSuchKeyException`** (a subclass of `AmazonS3Exception`): catch it by type. `DeleteObjectAsync` of a missing key succeeds (204).
- `MinioBuilder("<image>")` takes the image in its constructor (4.15 line); `GetConnectionString()` is the `http://host:port` endpoint, `GetAccessKey()`/`GetSecretKey()` the credentials; create the bucket with a raw `AmazonS3Client` in the fixture. A `Uri` options property binds from a configuration string; `minio:9000` binds as an absolute URI with scheme `minio`.
- **Mutation runs:** `if (false)` and `when (false)` fail the build (CS0162, CS8360 are errors under `TreatWarningsAsErrors`), so a red run needs a runtime-false condition (`services.Count < 0`, `when (key.Length < 0)`). Scratchpad `p127/mutate127.py` applies one replacement per run and restores in `finally`.

## What a new Kit package moves (#127, 2026-10-17; Archify 3.0.1)

- **A `PackageGraph.Table` row moves four diagrams:** execution-core (KeptOut ranges), kit-context (KeptOut, the Table span 17-74 and `inventory.json` 3-23 with its "21 packages" label and the "20 libraries" sublabel), messaging-and-delivery (KeptOut 90-93) and module-boundaries (the Table span). Rows added after the Testing row leave every per-package row's citation in place. Re-range with a regex anchored on the path, longest-moved range first (moving 94-95 to 96-97 before 96-97 to 98-99 made two matches). `p127/diagrams127.py` and `p127/repin127.py` (pin, finalize, delete receipts) did all four in one pass; every gate passed first time.
- A headless screenshot to check a page: a scratchpad Python script that runs Playwright's Chromium with `--headless=new --screenshot` (`p127/shot.py`); the guard refuses the binary path as a computed command name in Bash.
- `check-docs.py` fails a backticked `NoSuchKey` in `docs/architecture/README.md` as naming nothing: name the code's own type (`NoSuchKeyException`).

**Why:** each cost a run or a refused command in #127.
**How to apply:** for #128 (Azure Blob beside it, Azurite), #140 (Reporting over storage), any provider test against a container, and any ticket that adds a Kit package. Related: [[docs-verification-tools]], [[templates-packaging-and-publication-facts]], [[worktree-shell-and-check-gotchas]].
