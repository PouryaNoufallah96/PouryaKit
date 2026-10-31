# Private packages

The publication workflow uses the existing [Release package validation](packages.md), official artifact actions and `dotnet nuget push`.

## Versions and triggers

All Kit packages share one MinVer version ([the package set](packages.md#the-package-set)). The full-history checkout supplies tags to the existing build, tests and `pack --no-build` path. Publication downloads those packages and never rebuilds or overrides their version.

| Event | Version and behaviour |
|---|---|
| PR, tag push, integration branch (`feat/**`, `wave-**`) push or manual CI on a non-main branch | Read-only CI validates packages and consumers. No publication from these runs. |
| Main push | CI derives the MinVer Git-height prerelease, then successful CI authorizes private publication. |
| `v0.x` preview/rc tag on current main, followed by manual CI on main | CI fetches the tag and validates its exact MinVer version. Successful CI uses the same private publisher. |
| Manual private-publication run on main | Select an original successful main CI run, attempt and SHA. Reuse its preserved packages and manifest. |

Integration branch (`feat/**`, `wave-**`) pushes trigger read-only CI; other topic branch pushes do not, so use PR validation or [manual branch CI](ci.md) when hosted proof is needed. Publication triggers only on a completed `main` CI run (`branches: [main]` under `workflow_run`), so neither an integration branch run nor a non-main manual run can authorize it.

For a named version, choose and create an immutable tag such as `v0.2.0-rc.1` on the current main SHA with Git. Then select **CI**, **Run workflow**, branch **main**, or run:

```bash
gh workflow run ci.yml --ref main
```

Do this before main advances. Inspect the validation artifact's version before consuming it. A tag push alone only validates; publication accepts main CI runs, not tag-head runs. No workflow creates a tag or GitHub Release. The publisher accepts only `0.x` preview/rc versions.

## Publication and trust

[`private-publication.yml`](../.github/workflows/private-publication.yml) responds to completed CI on main, outside cancelable CI. Its read-only authorization job fetches the exact original run attempt using `actions/github-script`. It requires the fixed private repository, main default branch, CI workflow path, main source branch, same repository/head repository, selected SHA, a successful completed run and successful AI/package-validation jobs. The original event must be `push` or `workflow_dispatch`. A PR artifact or a failed validation cannot authorize publication.

The authorized job calls [`package-publication.yml`](../.github/workflows/package-publication.yml) with job permissions `contents: read`, `actions: read` and `packages: write`. Checkout uses the trusted main workflow revision with `persist-credentials: false`. Downloaded packages supply no executable code. Only this job receives package-write permission; the push/restore step uses its ephemeral `GITHUB_TOKEN`, with no PAT or new access setting.

`actions/download-artifact` downloads `package-validation-<run_id>-<run_attempt>` from the original run and rejects an artifact digest mismatch. The job binds `validation.json` to the authorized run, attempt, event and SHA; checks the version, source SHA and existing inventory; and checks every original package against `manifest.json`. The original artifact and manifest remain unchanged, with CI's existing 90-day retention. Missing or expired originals fail instead of triggering a replacement build.

The `pouryakit-private-publication` job group has `cancel-in-progress: false`. It covers download, verification, push and restore. GitHub retains one running and one pending member; a newer pending member can replace the older pending member. Re-run an evicted attempt if needed. This is serialization, not a durable queue. See [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

Publication uses only `https://nuget.pkg.github.com/PouryaNoufallah96/index.json`. The source credentials come from the job token through `NuGetPackageSourceCredentials_GitHub`; the temporary configuration contains no credential. Each original nupkg goes through the standard command:

```bash
dotnet nuget push "$package" --source GitHub --api-key "$GITHUB_TOKEN" \
  --skip-duplicate --no-symbols --configfile "$RUNNER_TEMP/publication.NuGet.Config"
```

`--skip-duplicate` treats an HTTP 409 as a skipped duplicate. It does not compare remote bytes with the manifest and does not prove identical-content recovery. Other push failures fail the job. After push, a temporary project references every library package in the inventory at its exact version and restores with a fresh package/cache directory. Source mapping requires `PouryaKit.*` from GitHub Packages and permits third-party dependencies from nuget.org. A package of inventory kind `template` is installed instead, since a `PackageReference` to it restores without assets and proves nothing about its templates: `dotnet new install PouryaKit.Templates@<version>` into a template hive of its own, then each template the package holds, by the `shortName` in `src/PouryaKit.Templates/content/*/.template.config/template.json`, is generated as `Acme.Shop` and as `acme-shop` and its solution restored with the same caches and configuration. `dotnet new install` reads the `NuGet.Config` of its working folder, where the `GitHub` source gets its credentials, and ignores source mapping, so it runs beside a copy of the configuration with nuget.org disabled. This checks private-feed availability and dependency resolution, not remote raw-byte identity or runtime behaviour; [package validation](packages.md#clean-consumers) builds and tests the generated solutions from the produced packages.

The diagram [From a push to the private feed](architecture/diagrams/ci-and-publication.html) draws this path, from the CI run that validates the packages to the feed check.

## Retry preserved originals

Re-run the publication workflow, or manually select an original main CI identity:

```bash
gh workflow run private-publication.yml --ref main \
  -f run_id=<original-run> -f run_attempt=<original-attempt> -f source_sha=<original-main-sha>
```

A retry skips what the feed already holds (HTTP 409) and pushes the rest. Preserve the original manifest and packages; never delete, replace or repack a published version. The duplicate skip and restore provide no remote raw-byte identity proof.

## Consume one complete version

Choose a version whose publication job completed its feed check, every library package restored and the template package installed from the feed, and use it for every Kit package reference. Developer restore uses an existing authorized classic token with `read:packages`; a consuming workflow can use its repository token where it already has package access. This workflow adds no developer credential or access grant. See [GitHub's NuGet authentication guide](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-to-github-packages).

Add the source and mapping to the consuming project's `NuGet.Config`, keeping credentials outside it:

```xml
<configuration>
  <packageSources>
    <add key="GitHub" value="https://nuget.pkg.github.com/PouryaNoufallah96/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="GitHub"><package pattern="PouryaKit.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Supply `NuGetPackageSourceCredentials_GitHub` through the shell or secret manager as `Username=<authorized-login>;Password=<read-token>;ValidAuthenticationTypes=Basic`. The source name and environment-variable suffix must agree. Consume packages without a Kit checkout or project references, as [package guidance](packages.md) describes.

## nuget.org publication

No workflow publishes to nuget.org. A version goes there by hand, as the original packages of a validated set, never rebuilt or repacked: verify the manifest's commit, version and SHA-256 hashes, then run `dotnet nuget push --skip-duplicate --no-symbols` on each original with a scoped API key entered at a hidden prompt. Check the result from nuget.org alone with fresh caches: restore every library package at its exact version, install `PouryaKit.Templates` and restore each generated solution. Run that check from a canonical path: on macOS the temporary folder is reached as both `/var/...` and `/private/var/...`, and a solution restored there fails with NuGet's `nuget.g.props' already exists`.

`0.1.0-preview.0.28` was published this way, to the private feed and to nuget.org, from a local run of `.agents/scripts/ci-local.sh`, so no CI run, receipt or artifact digest stands behind it. Its originals are kept outside the repository; never delete, replace or repack them. Its project and repository URLs point at the private repository, so they do not open for the public, its descriptions repeat the ID, and it has no readme, tags or icon. Changing any of them needs a new version.

A consumer needs nuget.org alone: `dotnet add package PouryaKit.Domain --version 0.1.0-preview.0.28`, and `dotnet new install PouryaKit.Templates@0.1.0-preview.0.28` for the templates.
