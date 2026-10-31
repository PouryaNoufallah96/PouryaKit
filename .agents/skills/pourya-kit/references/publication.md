# Private publication

Read [private package guidance](../../../../docs/private-packages.md) when consuming GitHub Packages, publishing a main candidate or retrying preserved originals.

Keep the existing shared Release build/test/pack path and the package inventory in `scripts/packaging/inventory.json`. MinVer alone determines versions from full history and tags; publication downloads originals and never rebuilds or supplies a version override. A named `0.x` preview/rc version uses a `v` tag created by hand on current main followed by manual CI on main. No workflow creates a tag or GitHub Release.

Authorize only the exact successful same-repository main CI `push` or `workflow_dispatch` attempt and SHA, with successful AI/package jobs. PR receipts cannot authorize publication. Use official artifact download, bind the receipt/manifest to that provenance, and verify local original hashes. Keep the original artifact and manifest unchanged, and fail if they are unavailable.

Use the fixed GitHub Packages feed, job-scoped `GITHUB_TOKEN` and `pouryakit-private-publication` group with cancellation disabled. Run standard `dotnet nuget push --skip-duplicate`, then restore every library package in the inventory at its exact version in a temporary consumer with fresh caches and GitHub source mapping for `PouryaKit.*`. Install a package of inventory kind `template` from the feed instead, because a `PackageReference` to it restores without assets: `dotnet new install <id>@<version>` into its own hive, run beside a copy of the configuration with nuget.org disabled (the install ignores source mapping and reads its working folder's `NuGet.Config`), then generate each template, by the `shortName` in its `template.json`, as `Acme.Shop` and `acme-shop`, and restore those solutions the same way. Credentials belong only in the push/restore environment, never configuration, artifacts or diagnostics. A newer pending attempt can replace an older pending attempt; GitHub concurrency is not a durable queue.

HTTP 409 skips are not remote-byte identity proof, and the SDK's package validation is no compatibility approval. Local workflow checks prove neither feed access nor private visibility.

Preserve the original artifacts and the retry record; never delete, replace or repack a published version. A retry proves availability, not remote raw-byte identity.
