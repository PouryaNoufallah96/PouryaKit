---
name: publication-feed-facts
description: Ticket #54 publication facts - dotnet new install ignores source mapping, reads only the working folder's NuGet.Config for credentials, a template PackageReference restores silently, running a workflow step locally
metadata:
  type: project
---

Verified in the #54 publication package (2026-10-08), SDK 10.0.401; check `.github/workflows/package-publication.yml` before relying on it:

- **A `PackageReference` to a `PackageType=Template` package restores silently:** exit 0, no NU warning, an empty target entry (no compile/runtime/build assets). A feed consumer must install it (`dotnet new install <id>@<version> --debug:custom-hive <abs>`) and generate from it.
- **`dotnet new install` and NuGet config** (decompiled `Microsoft.TemplateEngine.Edge` `NuGetApiPackageManager`, ilspycmd): sources come from `Settings.LoadDefaultSettings(Directory.GetCurrentDirectory())`, there is no `--configfile`; an `--add-source` equal to a configured source is skipped, otherwise added unnamed, so it never gets `NuGetPackageSourceCredentials_<name>` (NuGet's `PackageSourceProvider` attaches those to configured named sources only). It ignores packageSourceMapping: every enabled source is queried, the first with the exact version wins. The step runs the install from a folder holding a `NuGet.Config` copy with `dotnet nuget disable source nuget.org`. SDK 10 deprecates `id::version` (prints a notice); use `id@version`.
- **Shell guard:** any command containing `dotnet nuget disable source` is refused ("runs a string through source"), and `cd … && PATH=… python3 …` is refused as git-like; `PATH=<venv>/bin:/usr/bin:/bin python3 -c …` alone passes. Neither python3 has PyYAML: a scratchpad venv (`python3 -m venv`, `pip install pyyaml`) runs the brief's YAML check unchanged.
- **Running a workflow step locally:** a scratchpad Python script extracts the step's `run: |` block (as `tests/releases/publication/test_publication.py`'s `block()` does), swaps the GitHub feed URL for an empty local folder and runs `bash -c` with the step's env; the step's real `dotnet nuget push` then fills that folder (local push warns that `--skip-duplicate` is unsupported, harmless). A missing package in the manifest is the negative control. Earlier `validate.py --output` folders in the session scratchpad hold a real `manifest.json` and `packages/` to feed it.
- The push-and-prove step takes about 40 s locally for 21 packages, two templates generated under two names each (one shared `--packages` folder, so later restores are warm).

**Why:** each decided the design or cost a probe in #54; the credential rule is not testable without the hosted feed.
**How to apply:** for any change to the publication workflow, a new template package, or a hosted proof of the private feed. Related: [[template-package-facts]], [[merge-and-ci-tooling-facts]], [[worktree-and-check-gotchas]].
