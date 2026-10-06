# Named-release preflight

Read [the preflight guide](../../../../docs/releases-preflight.md) when selecting a named preview/rc version, preparing a baseline, reviewing SDK compatibility findings or consuming the preflight result in a release workflow.

Consume the shared package proof's inventory and preserved manifest/package bytes. Prepare named candidate artifacts under a scratch tag before touching production refs. MinVer is the sole version engine. Use the SDK validator through `scripts/releases/preflight/preflight.py`; never infer compatibility from source builds or suppression generation.

Keep strict SemVer ordering separate from MinVer generation. The SDK loader must establish candidate and baseline reference availability before API comparison or review, including first releases. A loader failure cannot be approved as an API finding. Its filename-based resolution does not prove dependency version identity.

A failed result with findings is a review request. Disposition every exact finding with its required increment, reviewer and rationale; reviewed breaks also need migration notes. Bind the review to the unchanged candidate/baseline identity objects from the result. A changed hash requires a fresh review.

Only observed history can make a passed preflight eligible for a production cut. `--history` is always a fixture and cannot prove hosted baseline absence or publication readiness. Owner approval, green-main provenance, current history/conflict checks, serialization and content-verified publication remain separate workflow gates. Preserve the result and artifacts; SDK API checks do not prove semantic compatibility.
