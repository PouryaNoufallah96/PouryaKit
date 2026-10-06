# CI package validation

The `CI` workflow runs on pushes and `pull_request`, with no branch filter. `ai-setup` retains the existing AI configuration check. `build-and-test` calls [the reusable package workflow](../.github/workflows/package-validation.yml) from the caller's own revision. It runs the [shared Release validation path](packages.md), including the existing Domain, dispatcher, validation, Structure and PostgreSQL Proving Sample tests, all thirteen packages and fifteen clean consumers. Debug build/test commands are replaced by these same checks against Release output. `publication-protocol` runs local HTTP fixtures and isolated red controls with read-only permissions and no feed credential input; it performs no real publication.

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

The separate [private publication workflow](private-packages.md) consumes this exact original-artifact contract. It checks successful main AI/package/protocol jobs through GitHub metadata, restores the immutable artifact ID and keeps publication privileges outside PR validation and cancelable CI. Hosted package publication proof remains pending.

## Hosted proof boundary

On 2026-10-06, [temporary same-repository PR #30](https://github.com/PouryaNoufallah96/PouryaKit/pull/30) exercised the reviewed integration commit `be507881ac1aa6727b2fb830f91a72a5c1c14a3e`. It closed without merging. The proof branch alone introduced an undefined `IntentionalHostedConsumerFailure()` in the single-Module consumer fixture, then reverted it.

| PR scenario | Hosted run | Actual tested merge SHA | Result | Artifact ID |
|---|---|---|---|---|
| Initial validation | [37448645751](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37448645751) | `b47372f2cc28feae58f2fb2f2f01f0cd98b825d8` | Success | `11404508475` |
| Deliberate consumer failure | [37449281223](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37449281223) | `49ff3f2cbd1900136745964bdeadaceb1aa496aa` | Failure, `CS0103` | `11405776196` |
| Repaired validation | [37449774763](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37449774763) | `165d1d82b559454bc2bc214d867aab72b4649ed4` | Success | `11406765985` |

All three receipts distinguish their tested merge SHA from PR heads `1adba0fc2f49ef5b8a1b164133ab84506e6a0cd7`, `e67fff74b368067eaf5c869a65ff1080b6d099b6` and `112e47a6510bab3d03a6b359f6a379195b52251e`, respectively. Their base stays at the reviewed integration SHA. Actual checkout and shared-path logs confirm full history, all three existing Spike tags, `persist-credentials: false` and `--no-fetch`. MinVer produced `0.1.0-preview.0.8` in all three runs; PR merge height, not a run number, determines this version.

Both successful artifacts contain thirteen nupkgs whose downloaded SHA-256 values match their source-SHA/version manifests, fifteen clean consumer results and a PostgreSQL runtime suite with 386 succeeded, zero failed and zero skipped tests. The failed run also passed the runtime suite, then failed specifically in the single-Module consumer. Its package step, job and overall CI result failed; diagnostic collection/upload passed. Its receipt has no successful version or manifest pointer, and no manifest exists. Artifact names are `package-validation-<run_id>-1`; their uploaded sizes were 608,373, 499,201 and 608,302 bytes, with caches and consumer configuration absent.

Every run's setup logs show only `Contents: read` and `Metadata: read` for both actual jobs, `ai-setup` and `build-and-test / validate`; no publication job ran. The [integration push run](https://github.com/PouryaNoufallah96/PouryaKit/actions/runs/37448605551) also passed at exact SHA `be507881ac1aa6727b2fb830f91a72a5c1c14a3e` with those same two read-only jobs. Repository secrets/environment metadata contained zero entries. The source workflow forwards no release credentials; this inspection complements the observed token permissions rather than proving arbitrary secret absence by logging environment values. Before/after API records confirm main, tags, GitHub Releases and private-fork settings unchanged. This experiment performs no package-feed publication or verification.

Real private fork PR validation was not executed. The [owner accepted this exception](https://github.com/PouryaNoufallah96/PouryaKit/issues/26#issuecomment-6013184398) because no second account was available and prohibited access changes. Same-repository hosted PR validation and permission/credential isolation were observed; they do not prove fork execution. Repository access remains unchanged. All four private-fork settings remained false, including fork workflow execution, write-token forwarding and secret/variable forwarding; that settings receipt is static metadata, not a hosted fork result.
