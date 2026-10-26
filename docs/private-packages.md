# Private packages

The owner narrowed Phase 5 on 2026-10-06 to clean packaging, MinVer versioning and private NuGet publication. The workflow uses the existing [Release package validation](packages.md), official artifact actions and `dotnet nuget push`. Named-cut automation, compatibility policy and content-verified recovery from #28/#29 are closed as not planned and require a new scoped ticket. This scope does not claim all original #24 acceptance criteria.

## Versions and triggers

MinVer is the only version engine. All Kit packages in `scripts/packaging/inventory.json` use the same version, with minimum `0.1`, tag prefix `v` and default prerelease identifiers `preview.0`. The full-history checkout supplies tags to the existing build, tests and `pack --no-build` path. Publication downloads those packages and never rebuilds or overrides their version.

| Event | Version and behaviour |
|---|---|
| PR, tag push, integration branch (`feat/**`, `wave-**`) push or manual CI on a non-main branch | Read-only CI validates packages and consumers. No publication from these runs. |
| Main push | CI derives the MinVer Git-height prerelease, then successful CI authorizes private publication. |
| Owner-created `v0.x` preview/rc tag on current main, followed by manual CI on main | CI fetches the tag and validates its exact MinVer version. Successful CI uses the same private publisher. |
| Manual private-publication run on main | Select an original successful main CI run, attempt and SHA. Reuse its preserved packages and manifest. |

Integration branch (`feat/**`, `wave-**`) pushes trigger read-only CI; other topic branch pushes do not, so use PR validation or [manual branch CI](ci.md) when hosted proof is needed. Publication triggers only on a completed `main` CI run (`branches: [main]` under `workflow_run`), so neither an integration branch run nor a non-main manual run can authorize it. The Wave 2 set, `PouryaKit.Templates` included, was published with the merge of `feat/wave-2` to `main` ([Hosted completion](#hosted-completion)), and its feed check installs that template package rather than referencing it ([below](#publication-and-trust)).

For a named version, choose and create an immutable tag such as `v0.2.0-rc.1` on the current main SHA using the normal owner-controlled Git process. Then select **CI**, **Run workflow**, branch **main**, or run:

```bash
gh workflow run ci.yml --ref main
```

Do this before main advances. Inspect the validation artifact's version before consuming it. A tag push alone only validates; publication accepts main CI runs, not tag-head runs. No workflow creates a tag or GitHub Release. Stable versions require a later owner decision; the publisher accepts only `0.x` preview/rc versions. The owner published `0.1.0-preview.0.28` to nuget.org once by hand ([nuget.org publication](#nugetorg-publication)); no workflow publishes there.

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

`--skip-duplicate` treats an HTTP 409 as a skipped duplicate. It does not compare remote bytes with the manifest and does not prove identical-content recovery. Other push failures fail the job. After push, a temporary project references every library package in the inventory at its exact version and restores with a fresh package/cache directory. Source mapping requires `PouryaKit.*` from GitHub Packages and permits third-party dependencies from nuget.org. A package of inventory kind `template` is installed instead, since a `PackageReference` to it restores without assets and proves nothing about its templates: `dotnet new install PouryaKit.Templates@<version>` into a template hive of its own, then each template the package holds, by the `shortName` in `src/PouryaKit.Templates/content/*/.template.config/template.json`, is generated as `Acme.Shop` and as `acme-shop` and its solution restored with the same caches and configuration. `dotnet new install` reads the `NuGet.Config` of its working folder, where the `GitHub` source gets its credentials, and ignores source mapping, so it runs beside a copy of the configuration with nuget.org disabled. This checks private-feed availability and dependency resolution, not remote raw-byte identity or runtime behaviour; [package validation](packages.md#clean-consumers) builds and tests the generated solutions from the produced packages. The template install ran against a local feed stand-in first ([acceptance record](acceptance.md#wave-2-private-feed-publication-covers-the-template-package-ticket-54)), and against GitHub Packages in the first `main` publication after the merge of `feat/wave-2`, its hosted proof ([acceptance record](acceptance.md#wave-2-hosted-publication-from-main)).

The diagram [From a push to the private feed](architecture/diagrams/ci-and-publication.html) draws this path, from the CI run that validates the packages to the feed check.

## Retry preserved originals

Re-run the publication workflow, or manually select an original main CI identity:

```bash
gh workflow run private-publication.yml --ref main \
  -f run_id=<original-run> -f run_attempt=<original-attempt> -f source_sha=<original-main-sha>
```

The first hosted attempt left Domain `0.1.0-preview.0.10` visible in the feed. Successful [retry run 37465070135](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465070135) completed that version from original main CI run `37456433718`, attempt `1`, artifact `11408539884`, source `2de265641b8cd94a5ddc17980cdcad530f0a8f60`: Domain returned HTTP 409 and was skipped, the remaining twelve packages were pushed, and all thirteen restored with fresh caches. Preserve the original manifest/packages and retry record; never delete, replace or repack that version. The duplicate skip and restore provide no remote raw-byte identity proof.

## Hosted completion

The active Phase 5 scope completed on 2026-10-06 at main commit `b234f4f6c3ac113cf3fd8d874b88d20571480cef`. [CI run 37465576160](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37465576160) passed 386 runtime tests with zero failures or skips, validated thirteen packages and built fifteen clean consumers at MinVer `0.1.0-preview.0.13`. [Publication run 37466084084](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37466084084) published those original packages and completed the clean thirteen-package private-feed restore. This proves publication and feed availability for that version, with the byte-identity and fork-execution limits above and in [CI guidance](ci.md#hosted-proof-boundary). It predates the Wave 2 packages and the template install.

Wave 2 completed on 2026-10-09 at main commit `2938afa5957807a7f4797d3a545df31931cd6ea6`, the merge of `feat/wave-2` (pull request #65). [CI run 37890237262](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37890237262) validated 21 packages and built 24 clean consumers, the two generated from the templates among them with their smoke tests, at MinVer `0.1.0-preview.0.17`. [Publication run 37891060172](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37891060172) pushed those 21 original packages, none of them a skipped duplicate, then from the feed with fresh caches restored the 20 library packages at that exact version, installed `PouryaKit.Templates@0.1.0-preview.0.17` into a template hive of its own with nuget.org disabled, which registered `pouryakit-monolith` and `pouryakit-modular-monolith`, and generated each template as `Acme.Shop` and as `acme-shop` and restored the four solutions. This is the first hosted proof of the credentialed template install from GitHub Packages. The generated solutions are restored there, not built or tested: the validation of the same CI run builds them, runs their smoke tests and publishes their Hosts from the produced packages. The byte-identity and fork-execution limits above apply as before.

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

## Local publication

On 2026-10-26 the owner approved a one-time exception to the hosted path above: GitHub Actions was unavailable (billing), so the set was validated and published from a developer machine with the standard clients. **Hosted CI did not run for this version**: there is no CI run, receipt (`validation.json`), artifact digest or ephemeral job token behind it, and the machine was the owner's Mac, cleaned after the 2026-10-25 incident but not reinstalled, a residual risk the owner accepted.

- **Source:** `main` `4e9905c9841b021d33deab7b1777ce285b0d6aa8` (PR #179's merge), cloned fresh from GitHub, with no `public/` or `.vscode/` path and no -0700 commit since `8e69b777`.
- **Validation, once:** `.agents/scripts/ci-local.sh`, the local reproduction of both CI jobs, every step exit 0: `PASS 38 packages, 44 clean consumers (4 generated from templates, smoke tests passed), negative controls and MinVer proof; Release tests total 1381, succeeded 1378, failed 0, skipped 3`. MinVer `0.1.0-preview.0.28`; `manifest.json` SHA-256 `794e60033e063ff9493cf322900abb1f4f793817634a3c117aafab37bc9ca31d`.
- **Integrity:** before the push, the publisher's own checks: the manifest's commit, version and file names, its package set equal to `scripts/packaging/inventory.json`, and every nupkg's SHA-256 (38 OK).
- **Push:** the standard `dotnet nuget push --skip-duplicate --no-symbols` of each original package with a short-lived classic token (`write:packages`) entered at a hidden prompt: 38 pushed, none a duplicate. Before it, the feed held `PouryaKit.Domain` up to `0.1.0-preview.0.22` and no `PouryaKit.Realtime`.
- **Feed check, fresh caches:** every library package referenced at `[0.1.0-preview.0.28]` restored from the feed (37 packages); `dotnet new install PouryaKit.Templates@0.1.0-preview.0.28` from the feed with nuget.org disabled registered `pouryakit-modular-monolith`, `pouryakit-monolith` and `pouryakit-service`; `pouryakit-modular-monolith` generated as `Acme.Shop` restored. The restore of the second name, `acme-shop`, failed with NuGet's `nuget.g.props' already exists`: on macOS the temporary folder is reached as both `/var/...` and `/private/var/...`, the log shows each project restored under both paths, and the two writes collided. The remaining generated solutions were not restored from the feed; the validation above built and tested all four template consumers from the same package bytes.

The originals (38 nupkgs, `manifest.json` and the logs) are kept outside the repository; never delete, replace or repack this version. The byte-identity limits of [Publication and trust](#publication-and-trust) apply.

## nuget.org publication

On 2026-10-26 the owner published the same 38 originals of `0.1.0-preview.0.28`, those of the [local publication](#local-publication), never rebuilt or repacked, to nuget.org from a developer machine with the standard client and without GitHub Actions. The repository stays private; no tag, GitHub Release or workflow was added, and no workflow publishes to nuget.org.

- **Before the push:** the manifest's commit and version and all 38 SHA-256 hashes verified; none of the 38 IDs existed on nuget.org; every third-party dependency (55 version ranges) resolves on nuget.org, and the 19 dependencies between Kit packages are on the exact `0.1.0-preview.0.28`; each nupkg carries the MIT `LICENSE` and the third-party notices it was packed with, and `PouryaKit.Reporting` also Roboto's licence; no entry holds a local path, a PDB, a token or a key, and the templates' only password is the `localhost` development default.
- **Push:** `dotnet nuget push --skip-duplicate --no-symbols` of each original with a scoped API key entered at a hidden prompt: 38 created, one after the client retried a transient error. nuget.org warned that no package has a readme.
- **After the push:** all 38 listed on nuget.org; each downloaded package carries nuget.org's repository signature and, apart from that signature and its content-types part, the originals' exact entries. From nuget.org alone, with fresh caches and a canonical path: the 37 library packages restored at `[0.1.0-preview.0.28]`, `PouryaKit.Templates@0.1.0-preview.0.28` installed `pouryakit-modular-monolith`, `pouryakit-monolith` and `pouryakit-service`, and each, generated as `Acme.Shop` and as `acme-shop`, restored. The local publication's `acme-shop` collision was macOS's `/var` symlink, not the packages: a solution of two plain class libraries fails the same way under `/var`, and a canonical path restores both names. A second anonymous check the same day found all 38 listed, restored the 37 library packages and installed the templates.
- **Known limits:** the packages' project and repository URLs point at this private repository, so they do not open for the public; the descriptions repeat the ID, and no package has a readme, tags or an icon. Changing any of them needs a new version.

A consumer needs nuget.org alone: `dotnet add package PouryaKit.Domain --version 0.1.0-preview.0.28`, and `dotnet new install PouryaKit.Templates@0.1.0-preview.0.28` for the templates.
