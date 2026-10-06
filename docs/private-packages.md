# Private packages and recovery

Publication uses only `https://nuget.pkg.github.com/PouryaNoufallah96/index.json`. Internal main packages have the preserved MinVer `0.x` prerelease version and receive no version tag or GitHub Release. Nuget.org publishing and stable 1.0 remain separate owner decisions. [Package validation](packages.md) owns the thirteen-package inventory and original checksum manifest; [CI](ci.md) owns their tested-source provenance.

## Consume one verified version

Select the same fully verified version for every Kit package reference. Restore credentials come from an authorized developer's classic token with `read:packages`, or a consuming workflow's repository token where it has package access. Publication uses the Kit workflow's ephemeral repository token; this process adds no PAT or access-setting change. See [GitHub's NuGet authentication guide](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-to-github-packages).

Add this source to the consuming project's `NuGet.Config`, alongside nuget.org for third-party dependencies. Keep credentials outside the file:

```xml
<configuration>
  <packageSources>
    <add key="GitHub" value="https://nuget.pkg.github.com/PouryaNoufallah96/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="GitHub"><package pattern="PouryaKit.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Supply `NuGetPackageSourceCredentials_GitHub` through the shell or secret manager as `Username=<authorized-login>;Password=<read-token>;ValidAuthenticationTypes=Basic`. The source name and environment-variable suffix must agree. Build the consumers without a Kit checkout or project references, as described in [package guidance](packages.md). Private feed access and package availability still require hosted verification below.

## Main publication

[`private-publication.yml`](../.github/workflows/private-publication.yml) responds to a completed `CI` run on main. It is separate from cancelable CI. Its read-only authorization job requires a same-repository main `push`, exact source SHA and run attempt, and successful `ai-setup`, `build-and-test / validate` and `publication-protocol` jobs. Failed shared checks cause the privileged publisher job to be skipped. Overall producer-run success is deliberately unnecessary, so recovery remains possible when a publisher interrupted an otherwise successful validation or another job is still running. The authorization job also retains its bounded diagnostics when it rejects a source run.

The reusable [`package-publication.yml`](../.github/workflows/package-publication.yml) job grants only `contents: read`, `actions: read` and `packages: write`. Checkout selects the trusted main workflow revision with `persist-credentials: false`; package artifacts never supply executable publisher code. Its token is available only to the publisher step. PR validation forwards no release credentials and cannot enter this workflow's main guard.

The job holds `pouryakit-private-publication` with `cancel-in-progress: false` through artifact recovery, remote reads, conflict checks, missing-only uploads and final verification. Future named cuts must use this same literal group at the publisher job only. A caller acquiring the same group would deadlock its reusable child. GitHub's default concurrency retains one pending member; a newer pending attempt replaces the older pending attempt. It preserves an active publisher, but is not a durable queue for three overlapping attempts. Re-dispatch an evicted pending recovery with its original artifact identity. See [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency). No unsupported queue key or lint suppression is added.

## Original artifacts and verification

Before publication, CI uploads `package-validation-<run_id>-<run_attempt>` with `validation.json`, `revision.json`, `history.json`, `package-evidence.json`, `manifest.json` and the original `packages/*.nupkg`. The manifest stays `{commit, version, packages:[{id,file,sha256}]}`. It is not rewritten by publication. CI retains it for 90 days.

Recovery locates that exact immutable artifact ID in the original run's fully paginated listing. It checks run, attempt, event, branch, repository, SHA, successful jobs, artifact name, workflow-run linkage, expiration and archive SHA-256. A different ID, expired artifact, missing manifest or bytes, and mismatched receipts fail. Downloads follow the short-lived [artifact API redirect](https://docs.github.com/en/rest/actions/artifacts#download-an-artifact) without forwarding the repository authorization to storage or retaining the signed URL. The extracted archive is bounded and rejects escaping paths and symlinks. Recovery never runs build, pack, MinVer or a source-artifact script.

[`publisher.py`](../scripts/releases/publication/publisher.py) validates the original thirteen-package inventory, filenames, source SHA, version, hashes and nuspec repository/ID/version/commit before a feed request. It discovers `PackageBaseAddress/3.0.0` and `PackagePublish/2.0.0` from the authenticated fixed service index. The [NuGet push protocol](https://learn.microsoft.com/en-us/nuget/api/package-publish-resource) receives the original nupkg as the first multipart item. It never recreates an archive or uses a restore-cache package as hash evidence.

The publisher enumerates authenticated package metadata and each expected ID's NuGet version index. API authorization failures and intermediate pagination failures are errors, never an empty feed. A successful empty owner REST listing alone does not establish private version-history absence. Existing versions are downloaded and checked against the original raw-byte hash, nuspec identity and actual `visibility: private` with repository linkage. All existing members must pass before the first missing upload. The publisher accepts any HTTP 200–299 upload response, matching [NuGet.Client's success check](https://github.com/NuGet/NuGet.Client/blob/c1d4dfc717af3a4e2545db087411f5ddf5e7d389/src/NuGet.Core/NuGet.Protocol/Resources/PackageUpdateResource.cs#L514), and confirms the exact raw bytes and private linkage before continuing. It then downloads and verifies the complete thirteen-member set again. An HTTP 409 is accepted only after content verification. Different source SHA or bytes are conflicts and never overwritten.

Only the discovered raw nupkg GET opts into storage redirects. Each redirected request carries no initial authorization, API key or other caller headers. A chain allows at most three redirects, each checked against the existing HTTPS storage-host families, without userinfo, fragments or unsupported ports. API, feed-index and upload redirects remain refused. The artifact ZIP redirect uses the same bounded credential-free storage checks. An unsupported destination fails with only numeric status and sanitized hostname diagnostics; signed paths and queries remain in memory only.

Publication receipts retain original run/attempt/artifact ID/name/digest/expiration, SHA/version, discovered resource URLs, bounded observed package metadata, each expected ID's complete version index, missing IDs, upload response statuses recorded before verification, accepted uploads, confirmed uploads and final verified hashes/private metadata. Redirect diagnostics retain only numeric status, source/target hostnames and followed/refused result. GETs have bounded retries; each upload is attempted once because a lost response may have followed a successful write. Recover that uncertainty from the same original artifact. Visibility/content polling is bounded. Receipts survive failure and contain neither tokens, signed URLs, credential files nor raw upstream error bodies.

An upload may leave one package when subsequent content or private metadata checks fail. Stop there and inspect the receipt; do not continue or claim a private set. Specified-user package REST metadata under a repository token and exact raw-byte preservation on the real feed remain hosted uncertainties. The implementation fails closed if these checks cannot establish the required facts.

## Recover an interrupted attempt

The dispatch workflow must first exist on default main. Preserve the original successful main run ID, attempt, immutable package-validation artifact ID and exact source SHA. After approval to publish that reviewed candidate, invoke:

```bash
gh workflow run private-publication.yml --ref main \
  -f run_id=<original-run> -f run_attempt=<original-attempt> \
  -f artifact_id=<original-artifact-id> -f source_sha=<exact-original-sha> -f proof=none
```

This invokes the same locked publisher and original bytes. Inspect the `private-publication-<publisher-run>-<attempt>` receipt artifact. Success requires `result: success`, `complete: true`, and thirteen original matching hashes with observed private metadata. An identical repeat verifies all thirteen and reports zero uploads. Missing or expired original evidence is a hard failure; a fresh build is a different candidate, never a retry. Even repacking the same SHA/tag/version can change ZIP timestamps and raw hashes, so recovery always needs the preserved original bytes.

The reusable core for named releases is `publisher.publish(candidate_directory, externally_verified_sha, client, receipt, save)`. #29 owns approval, exact green-main linkage, tagged validation, compatibility/progression and immutable tag checks before invoking it. Its tagged artifacts need their own provenance wrapper, under the same publisher group; the main-recovery wrapper intentionally accepts main-push CI originals only. The core reuses [preflight's strict SemVer parser](releases-preflight.md), accepting major 0 with the first prerelease identifier `preview` or `rc`, including bare channels and alphabetic suffixes. It requires the exact preserved MinVer PackageVersion; build metadata is rejected because MinVer excludes it from package/build versions. It does not calculate another version.

## Hosted proof plan and current boundary

Local HTTP fault tests exercise the full archive recovery and feed protocol with actual nupkg bytes, hashes and source identity. They prove the implementation's decisions. They do not prove private visibility, raw-byte preservation or hosted serialization.

[Main validation 37456433718](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37456433718), attempt 1 at `2de265641b8cd94a5ddc17980cdcad530f0a8f60`, succeeded and retained original artifact `11408539884` at MinVer `0.1.0-preview.0.10`. [Automatic publication 37456894327](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37456894327) attempted only Domain, then rejected its upload response; the exact HTTP status was not recorded. [Read-only probe 37457107690](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37457107690) observed that Domain version but refused its raw-content redirect; the target was not recorded. Owner REST enumeration was empty despite that version index. This is partial publication with no verified remote hash or actual private-linkage row. The local repair accepts the client's 2xx success contract and scoped credential-free redirects; it does not establish either unrecorded hosted value. Hosted recovery, private bytes/metadata and serialization remain pending. Stop rather than infer privacy or rebuild these originals.

The dispatch offers bounded proof choices, off by default: `probe` performs the authenticated read/check pass without uploads and returns `probe-success` with `complete: false`; `fail-after-one` fails after exactly one newly accepted upload has passed raw-byte and private checks; `hold` waits 45 seconds inside the shared publisher lock; `negative-content` changes a recovered local copy and its copied hash, then checks the actual remote conflict through a read-only pass; `negative-sha` changes only the recovered manifest SHA; `missing-credential` omits the publisher-step token. Negative content proof always fails and performs no uploads, including when the feed has no matching original. No choice changes packages already in the feed.

Root reviews and approves the concrete main promotion and publication consequences before execution. Use this sequence:

1. Bootstrap reviewed workflows on main. A reviewed temporary main change can suppress the automatic publisher job during this first bootstrap, allowing the read-only `probe` against a fresh successful main validation artifact. Record authenticated service resources, package metadata routes, version indices and artifact archive digest. If package REST access is unavailable, stop and report that boundary without adding a PAT or changing access.
2. Restore the reviewed automatic invocation and observe one successful main push publication. The first original upload must pass raw-byte and actual private metadata checks before the publisher uploads the other twelve. Save the original artifact identity and all thirteen remote hashes, plus unchanged tag and GitHub Release census. An approved main-promotion decision must include this publication.
3. For a second fresh main MinVer version, temporarily set only the main caller's `proof` argument to `fail-after-one` in a reviewed main workflow change. Its successful shared CI produces the fresh version; automatic publication confirms one original upload, records one confirmed member and fails. Dispatch `proof=none` using that exact run/attempt/artifact/SHA to complete twelve missing uploads. Repeat identically and assert zero uploads. Restore the caller's default before further work. No synthetic versions or deliberately corrupt feed packages are needed.
4. Run the negative-copy choices, an unavailable original artifact ID and `missing-credential` against known originals. Each must fail before a conflicting write. Compare the feed's original full-set hashes afterward. These hosted local-copy controls complement the protocol test of conflicting remote bytes and source SHA; they do not manufacture a corrupt remote package.
5. Dispatch two identical recoveries, the first with `hold` and the second with `none`. Record reusable publisher job start/finish timestamps and queued/running/completed states. Both must finish with no active cancellation and thirteen verified originals. A replaced pending member is not success evidence.
6. To prove failing-main suppression, review a temporary main workflow step immediately before the shared validation command that changes the single-Module consumer fixture on the runner. The shared path's existing clean-checkout guard rejects this proof-only dirty tree before consumer compilation. Record that actual failing check, not a compiler failure. Its main CI receipt has no successful manifest and the separate privileged publisher is skipped by its authorization job. Revert the temporary workflow step, observe green shared validation, and preserve the before/after source/version identities. This temporary invocation change is a separate reviewed hosted experiment, absent from the shipped workflows. It leaves the clean-checkout guard intact; #26 separately supplies the actual hosted `CS0103` consumer failure proof.

Maintain an evidence table per scenario with event/run URL, source and tested SHA, attempt, MinVer version, artifact ID/digest, receipt result, uploaded/confirmed count, final hashes/private metadata and tag/release census. Until those hosted rows exist, #27 remains open. Value: a traceable private set and safe recovery. Cost: one protocol publisher and retained evidence checks. Impact: package delivery only; the Kit's runtime semantics stay under the existing ADRs.
