# Private publication

Read [private package guidance](../../../../docs/private-packages.md) when consuming GitHub Packages, publishing a main candidate, recovering an interrupted attempt or wiring a named release to the shared publisher.

Publication takes the original validation artifact ID, run ID/attempt and exact SHA. A successful PR receipt cannot authorize it. Require actual successful trusted-main AI, shared package and publication protocol jobs; overall producer-run success is unnecessary for recovery. `scripts/releases/publication/run.py` restores those originals and never builds replacement packages. Repacking the same SHA/version can change ZIP timestamps and raw hashes.

Use the fixed GitHub Packages feed and `pouryakit-private-publication` job group with cancellation disabled. The lock covers every remote read, conflict check, upload and final verification. There is one pending slot; retain the original identity if another pending attempt replaces it. Keep credentials out of checkout, artifacts and diagnostics.

Completion is thirteen original raw-byte hashes plus observed private package metadata, recorded in the publication receipt. Check every existing member before uploading missing members. A conflict or unavailable original is a failure. Named cuts reuse `publisher.publish`; #29 supplies tagged-artifact provenance and its release gates before calling that core under the same lock. MinVer owns the version.

Use proof options only for an explicitly approved hosted experiment. Read-only `probe` is not completed publication. Local protocol tests do not establish real feed access, visibility, byte preservation or hosted serialization; retain that boundary until hosted receipts exist.
