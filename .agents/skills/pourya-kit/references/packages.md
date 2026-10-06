# Package consumption and validation

Read [the package guide](../../../../docs/packages.md) before changing package metadata, inventory, versions or consumer checks. The explicit inventory and existing `PackageGraph` are the owners of the package set and dependency rules. MinVer is the sole version engine; select one manifest version for all Kit references.

Run `python3 scripts/packaging/validate.py` from the Kit checkout with the pinned SDK, Python and Docker. It prints a system-temporary evidence directory with the thirteen nupkgs, source SHA/version/checksum manifest, separate PostgreSQL test logs and isolated consumer results. `--output` accepts a fresh evidence directory outside the checkout.

For a consuming project, use that directory's `packages/` as a local feed, map `PouryaKit.*` to it, and keep nuget.org for third-party packages. Use the version recorded in `manifest.json`. The shared proof builds each package alone, the documented single-Module set and the copied Proving Sample using only package references for Kit dependencies.

Keep source runtime tests, package builds and hosted publication evidence distinct. This path verifies artifacts and local consumers. It does not publish, establish a named-release compatibility baseline or prove private-feed recovery; those are later Phase 5 work. Stable 1.0 and public publishing still require the owner's later decisions.
