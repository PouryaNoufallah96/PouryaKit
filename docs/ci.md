# CI package validation

The `CI` workflow runs on pushes and `pull_request`, with no branch filter. `ai-setup` retains the existing AI configuration check. `build-and-test` calls [the reusable package workflow](../.github/workflows/package-validation.yml) from the caller's own revision. It runs the [shared Release validation path](packages.md), including the existing Domain, dispatcher, validation, Structure and PostgreSQL Proving Sample tests, all thirteen packages and fifteen clean consumers. Debug build/test commands are replaced by these same checks against Release output.

## Checkout and credentials

Checkout explicitly selects `github.sha` with `fetch-depth: 0`, `fetch-tags: true` and `persist-credentials: false`. The checkout action uses the read-only repository token to fetch the private repository, then removes its credentials. The shared command receives no token:

```bash
python3 scripts/packaging/validate.py --no-fetch --output "$EVIDENCE_DIR"
```

`EVIDENCE_DIR` is a fresh directory under the runner's temporary directory, outside the checkout. `--no-fetch` uses history already fetched by authenticated checkout and rejects shallow repositories. `history.json` records that mode, the checkout SHA and available tags. The ordinary local command still fetches origin. No version is supplied by CI; MinVer derives the shared build/package version from this history.

For a PR, `github.sha` is the merge revision, not its head commit. `revision.json` checks the actual `HEAD` against that intended SHA and records the event, ref, repository, run ID/attempt, PR head/base SHAs and head repository. The job summary labels the tested SHA and PR head separately. Push validation tests the exact pushed SHA. GitHub documents [PR merge revision semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request) and [same-revision reusable calls](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows#calling-a-reusable-workflow).

Both workflows and the reusable job grant only `contents: read`; unspecified token permissions are disabled. The reusable workflow has no secret inputs, inherited secrets or environment. Validation has no package publication, tag creation or GitHub Release path. It uses `pull_request`, never `pull_request_target`. GitHub's artifact service credentials let the upload action retain diagnostic artifacts; they are separate from package and repository write permissions. See [workflow permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#permissions).

## Artifacts and downstream contract

On success or failure, the collector retains available packages, root validation logs, selected evidence JSON and consumer `obj/project.assets.json` files. It excludes NuGet caches, CLI homes, consumer configuration, raw webhook events and the mutated negative-control package sets. Symlinks that escape the evidence directory fail collection. Artifact names include the run ID and attempt, `package-validation-<run_id>-<run_attempt>`, with 90-day retention. Expired or unavailable artifacts cannot establish provenance for recovery.

The artifact contains:

| File | Meaning |
|---|---|
| `validation.json` | Repository, event/ref, run ID/attempt, tested SHA, PR identity, shared-path outcome, successful version and manifest location |
| `revision.json`, `history.json` | Intended/actual checkout agreement, non-shallow history, available tags and fetch mode |
| `manifest.json`, `packages/*.nupkg` | The successful shared path's source SHA/version/package IDs/filenames/SHA-256 record and original bytes |
| `*.log`, `evaluated-projects.json`, `minver.json` | Release/runtime/pack/consumer/control diagnostics and version proof |
| `package-evidence.json`, `consumers/**/obj/project.assets.json` | Package validation inputs and observed consumer closures; absolute paths identify the original runner, not a portable cache |

On success the collector checks that the manifest SHA equals the tested SHA and that every recorded package matches its checksum. A failed shared command remains a failed step/job, even if diagnostic collection and upload succeed. A failure receipt has no successful version or manifest pointer; partial packages remain diagnostics.

The reusable workflow exposes `tested_sha`, `version`, `validation_result`, `artifact_name` and immutable `artifact_id`. A later publisher must require successful validation and AI jobs, check the actual hosted workflow/job conclusions, and require a trusted `push` run for this repository on `refs/heads/main` at the selected SHA. It must match the run ID/attempt, receipt and manifest, then verify the original package hashes. A successful PR receipt identifies its tested merge SHA and is not authorization to publish. Receipt content alone is not a trust anchor because PR code can change its own validation. Separate-run release selection must verify provenance through GitHub's run metadata and trusted workflow revision, not a contributor-supplied artifact or claimed green SHA.

This ticket introduces no publisher. Future privileged jobs must keep their permissions and credentials separate from PR validation and gate on the trusted main result.

## Hosted proof boundary

Local focused tests and workflow lint prove the evidence contract and static workflow shape. They do not prove hosted token permissions or PR execution. Before closing #26, run the reviewed implementation in a same-repository PR, preserve a successful package run, commit a deterministic compile failure to the temporary proof branch, observe failed validation, then restore and observe green. Retain run URLs, job token-permission logs, merge/head/base SHAs, history/tags, manifest/version, artifact IDs and the absence of publication jobs. Close that proof PR without merging its deliberate failure.

Real private fork PR validation has not been executed. The [owner accepted this exception](https://github.com/PouryaNoufallah96/PouryaKit/issues/26#issuecomment-6013184398) because no second account was available and prohibited access changes. Same-repository hosted PR validation and permission/credential isolation are the required substitute evidence; they do not prove fork execution. Repository access and private-fork settings remain unchanged. Hosted success/failure/isolation proof is pending for this implementation.
