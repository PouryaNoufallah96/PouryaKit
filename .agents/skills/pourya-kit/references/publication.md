# Private publication

Read [private package guidance](../../../../docs/private-packages.md) when consuming GitHub Packages, publishing a main candidate or retrying preserved originals.

Keep the existing shared Release build/test/pack path and thirteen-package inventory. MinVer alone determines versions from full history and tags; publication downloads originals and never rebuilds or supplies a version override. A named `0.x` preview/rc version uses an owner-created `v` tag on current main followed by manual CI on main. No workflow creates a tag or GitHub Release.

Authorize only the exact successful same-repository main CI `push` or `workflow_dispatch` attempt and SHA, with successful AI/package jobs. PR receipts cannot authorize publication. Use official artifact download, bind the receipt/manifest to that provenance, and verify local original hashes. Keep the original artifact and manifest unchanged, and fail if they are unavailable.

Use the fixed GitHub Packages feed, job-scoped `GITHUB_TOKEN` and `pouryakit-private-publication` group with cancellation disabled. Run standard `dotnet nuget push --skip-duplicate`, then restore all thirteen exact packages in a temporary consumer with fresh caches and GitHub source mapping for `PouryaKit.*`. Credentials belong only in the push/restore environment, never configuration, artifacts or diagnostics. A newer pending attempt can replace an older pending attempt; GitHub concurrency is not a durable queue.

HTTP 409 skips are not remote-byte identity proof. Named-cut automation, compatibility/progression checks and content-verified recovery in #28/#29 are deferred by the owner's 2026-10-06 scope adjustment. Do not claim all original #24 acceptance criteria. Record actual hosted publication/consumer evidence in ticket #27; local workflow checks prove neither feed access nor private visibility.

Preserve the partial Domain `0.1.0-preview.0.10` publication and original main CI `37456433718`, attempt `1`, artifact `11408539884`, source `2de265641b8cd94a5ddc17980cdcad530f0a8f60`. Retry those original bytes; never delete, replace or repack that version.
