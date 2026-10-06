# Private publication

Read [private package guidance](../../../../docs/private-packages.md) when consuming GitHub Packages, publishing a main candidate, recovering an interrupted attempt or wiring a named release to the shared publisher.

Publication takes the original validation artifact ID, run ID/attempt and exact SHA. A successful PR receipt cannot authorize it. Require actual successful trusted-main AI, shared package and publication protocol jobs; overall producer-run success is unnecessary for recovery. `scripts/releases/publication/run.py` restores those originals and never builds replacement packages. Repacking the same SHA/version can change ZIP timestamps and raw hashes.

Use the fixed GitHub Packages feed and `pouryakit-private-publication` job group with cancellation disabled. The lock covers every remote read, conflict check, upload and final verification. There is one pending slot; retain the original identity if another pending attempt replaces it. Keep credentials out of checkout, artifacts and diagnostics.

Completion is thirteen original raw-byte hashes plus observed private package metadata, recorded in the publication receipt. Check every existing member before uploading missing members. A conflict or unavailable original is a failure. Named cuts reuse `publisher.publish`; #29 supplies tagged-artifact provenance and its release gates before calling that core under the same lock. MinVer owns the version.

Accept 2xx upload responses and 409 only with subsequent original-byte/private-linkage verification; record the numeric response before verification. Raw nupkg redirects opt into at most three checked HTTPS storage hops with no forwarded headers or credentials. Artifact ZIP downloads permit only one checked storage redirect. Other API/feed-index/upload redirects remain refused. Retain only status and sanitized hostnames, never signed URLs. An empty owner REST listing alone does not prove private history is empty.

Use proof options only for an explicitly approved hosted experiment. Read-only `probe` is not completed publication. Local protocol tests do not establish real feed access, visibility, byte preservation or hosted serialization; retain that boundary until hosted receipts exist.

The first hosted attempt left Domain `0.1.0-preview.0.10` visible in its version index, but rejected an unrecorded upload status and then an unrecorded raw-content redirect. Its remote hash/private linkage remain unverified. Preserve main run `37456433718`, attempt `1`, artifact `11408539884` and source `2de265641b8cd94a5ddc17980cdcad530f0a8f60` for recovery; do not infer the missing status/target or repack the same SHA.
