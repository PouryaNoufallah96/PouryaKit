#!/usr/bin/env python3
"""Real SDK/package/MinVer proof, entirely in external temporary repositories."""

import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[3]
ENTRY = ROOT / "scripts/releases/preflight/preflight.py"
spec = importlib.util.spec_from_file_location("preflight", ENTRY)
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


class PreflightTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.output = Path(tempfile.mkdtemp(prefix="pouryakit-preflight-tests-"))
        print(f"Evidence: {cls.output}", flush=True)
        cls.repo = cls.output / "source"
        cls.repo.mkdir()
        shutil.copyfile(ROOT / "global.json", cls.repo / "global.json")
        for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
            (cls.repo / name).write_text("<Project />\n")
        (cls.repo / "Fixture.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><PackageId>Preflight.Fixture</PackageId>
    <MinVerMinimumMajorMinor>0.1</MinVerMinimumMajorMinor><MinVerTagPrefix>v</MinVerTagPrefix>
    <MinVerDefaultPreReleaseIdentifiers>preview.0</MinVerDefaultPreReleaseIdentifiers>
    <RepositoryUrl>https://github.com/example/preflight-fixture</RepositoryUrl>
    <RepositoryType>git</RepositoryType><PublishRepositoryUrl>true</PublishRepositoryUrl>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="MinVer" Version="8.0.0" PrivateAssets="all" /></ItemGroup>
</Project>\n''')
        (cls.repo / ".gitignore").write_text("bin/\nobj/\n")
        cls.command(["git", "-C", cls.repo, "init", "-q"])
        cls.commits = {}
        for name, methods in {
            "baseline": 'public static int Method(string value) => 1;',
            "compatible": 'public static int Method(string value) => 2;',
            "additive": 'public static int Method(string value) => 2; public static int Method(string value, int count) => count;',
            "removed": 'public static int Method(string value, int count) => count;',
            "renamed": 'public static int Method(string text) => 2;',
        }.items():
            (cls.repo / "Api.cs").write_text(f"namespace Proof; public static class Api {{ {methods} public static System.Text.Json.JsonElement Json() => default; }}\n")
            cls.command(["git", "-C", cls.repo, "add", "."])
            cls.command(["git", "-C", cls.repo, "-c", "user.name=Preflight proof", "-c", "user.email=proof@example.invalid", "commit", "-qm", name])
            cls.commits[name] = cls.command(["git", "-C", cls.repo, "rev-parse", "HEAD"]).strip()
            if name == "baseline":
                cls.command(["git", "-C", cls.repo, "tag", "v0.1.0-preview.1"])
        cls.inventory = cls.output / "inventory.json"
        preflight.write_json(cls.inventory, {"packages": [{"id": "Preflight.Fixture"}]})
        cls.history = cls.output / "history.json"
        preflight.write_json(cls.history, {"named": [{"version": "0.1.0-preview.1", "commit": cls.commits["baseline"]}], "published": []})
        cls.artifacts = {}
        for name, source, version in [
            ("baseline", "baseline", "0.1.0-preview.1"),
            ("compatible", "compatible", "0.1.1-preview.2"),
            ("additive", "additive", "0.2.0-preview.2"),
            ("additive-patch", "additive", "0.1.1-preview.3"),
            ("removed", "removed", "0.2.0-rc.1"),
            ("removed-patch", "removed", "0.1.1-rc.1"),
            ("renamed", "renamed", "0.2.0-rc.2"),
        ]:
            artifact = cls.output / name
            checkout = cls.output / f"pack-{name}"
            cls.command(["git", "clone", "--no-hardlinks", "--no-checkout", cls.repo, checkout])
            cls.command(["git", "-C", checkout, "checkout", "--detach", cls.commits[source]])
            if name != "baseline":
                cls.command(["git", "-C", checkout, "tag", "v" + version, cls.commits[source]])
            cls.command(["dotnet", "pack", checkout / "Fixture.csproj", "-c", "Release", "-o", artifact / "packages", "--nologo"], cwd=checkout, log=cls.output / f"pack-{name}.log")
            file = next((artifact / "packages").glob("*.nupkg"))
            preflight.write_json(artifact / "manifest.json", {"commit": cls.commits[source], "version": version,
                "packages": [{"id": "Preflight.Fixture", "file": file.name, "sha256": preflight.digest(file)}]})
            cls.artifacts[name] = artifact
        cls.before = preflight.snapshot(cls.repo)
        cls.counter = 0
        guard_folder = cls.output / "guard-proof"
        guard_folder.mkdir()
        cls.guard, cls.framework_refs = preflight.reference_tool(guard_folder)

    @classmethod
    def command(cls, args, cwd=ROOT, log=None):
        return preflight.run(args, cwd=cwd, log=log).stdout

    def invoke(self, name, *, history=None, baseline=True, review=None, repo=None, candidate=None, version=None):
        type(self).counter += 1
        output = self.output / f"case-{self.counter:02d}-{name}"
        artifact = candidate or self.artifacts[name]
        requested = version or preflight.read_json(artifact / "manifest.json")["version"]
        command = ["python3", ENTRY, "--candidate", artifact, "--version", requested,
                   "--history", history or self.history, "--inventory", self.inventory,
                   "--repo", repo or self.repo, "--minver-project", "Fixture.csproj", "--output", output]
        if baseline:
            command.extend(["--baseline", self.artifacts["baseline"] if baseline is True else baseline])
        if review:
            path = self.output / f"review-{self.counter}.json"
            preflight.write_json(path, review)
            command.extend(["--review", path])
        process = preflight.run(command, allow_failure=True, log=self.output / f"case-{self.counter:02d}.log")
        result = preflight.read_json(output / "result.json")
        self.assertEqual(process.returncode, 0 if result["status"] == "passed" else 1)
        self.assertTrue(result["production_unchanged"])
        self.assertFalse(result["publication_eligible"], "Fixtures must never become hosted evidence")
        return result, output

    @staticmethod
    def review_for(result):
        return {"candidate": result["candidate"], "baseline": result["baseline"],
                "migration_notes": "Replace Method(string) calls with Method(string, int).",
                "dispositions": [{"key": finding["key"], "decision": finding["policy"],
                                  "reviewer": "Fixture reviewer", "rationale": "Intended and reviewed fixture API change."}
                                 for finding in result["findings"]]}

    def test_compatible_patch_compares_real_artifacts(self):
        result, _ = self.invoke("compatible")
        self.assertEqual(result["status"], "passed", result.get("error"))
        self.assertEqual(result["baseline_status"], "compared")
        self.assertEqual(result["findings"], [])
        self.assertEqual([row["exit_code"] for row in result["comparisons"]], [0, 0])
        self.assertEqual(result["minver"]["properties"]["PackageVersion"], "0.1.1-preview.2")

    def test_addition_requires_exact_disposition_and_minor(self):
        result, _ = self.invoke("additive")
        self.assertEqual(result["status"], "failed")
        self.assertTrue(any(finding["direction"] == "reverse" and finding["diagnostic"]["DiagnosticId"] == "CP0002" for finding in result["findings"]), result.get("error"))
        review = self.review_for(result)
        passed, _ = self.invoke("additive", review=review)
        self.assertEqual(passed["status"], "passed", passed.get("error"))
        patch, _ = self.invoke("additive-patch")
        rejected, _ = self.invoke("additive-patch", review=self.review_for(patch))
        self.assertIn("minor increment", rejected["error"])

    def test_break_requires_minor_notes_and_all_fresh_dispositions(self):
        result, output = self.invoke("removed")
        self.assertEqual(result["status"], "failed")
        forward = output / "sdk/Preflight.Fixture/forward/raw.log"
        self.assertIn("error CP0002", forward.read_text())
        review = self.review_for(result)
        passed, _ = self.invoke("removed", review=review)
        self.assertEqual(passed["status"], "passed", passed.get("error"))
        no_notes = json.loads(json.dumps(review))
        no_notes["migration_notes"] = "  "
        missing, _ = self.invoke("removed", review=no_notes)
        self.assertIn("migration notes", missing["error"])
        stale = json.loads(json.dumps(review))
        stale["candidate"]["packages"][0]["sha256"] = "0" * 64
        rejected, _ = self.invoke("removed", review=stale)
        self.assertIn("Review identities differ", rejected["error"])
        incomplete = json.loads(json.dumps(review))
        incomplete["dispositions"].pop()
        rejected, _ = self.invoke("removed", review=incomplete)
        self.assertIn("Every actual SDK finding", rejected["error"])
        patch, _ = self.invoke("removed-patch")
        rejected, _ = self.invoke("removed-patch", review=self.review_for(patch))
        self.assertIn("minor increment", rejected["error"])

    def test_first_release_and_missing_or_wrong_baseline(self):
        empty = self.output / "empty-history.json"
        preflight.write_json(empty, {"source": "github-observed", "named": [], "published": []})
        result, _ = self.invoke("compatible", history=empty, baseline=False)
        self.assertEqual(result["status"], "passed", result.get("error"))
        self.assertIsNone(result["baseline"])
        self.assertIn("fixture only", result["baseline_status"])
        self.assertEqual([row["direction"] for row in result["comparisons"]], ["intrinsic"])
        missing, _ = self.invoke("compatible", baseline=False)
        self.assertIn("baseline artifacts are required", missing["error"])
        wrong, _ = self.invoke("compatible", baseline=self.artifacts["additive"])
        self.assertIn("last named release", wrong["error"])

    def test_parameter_name_change_is_break_in_both_directions(self):
        result, _ = self.invoke("renamed")
        self.assertEqual(result["status"], "failed")
        self.assertEqual({finding["direction"] for finding in result["findings"]}, {"forward", "reverse"}, result.get("error"))
        self.assertTrue(all(finding["policy"] == "reviewed-break" for finding in result["findings"]))
        accepted, _ = self.invoke("renamed", review=self.review_for(result))
        self.assertEqual(accepted["status"], "passed", accepted.get("error"))

    def test_missing_and_changed_artifact_or_manifest(self):
        broken = self.output / "corrupt"
        shutil.copytree(self.artifacts["compatible"], broken)
        file = next((broken / "packages").glob("*.nupkg"))
        file.write_bytes(file.read_bytes() + b"changed")
        changed, _ = self.invoke("compatible", candidate=broken)
        self.assertIn("checksum differs", changed["error"])
        file.unlink()
        missing, _ = self.invoke("compatible", candidate=broken)
        self.assertIn("Package files differ", missing["error"])
        mismatch, _ = self.invoke("compatible", version="0.1.1-preview.3")
        self.assertIn("does not equal the requested version", mismatch["error"])

    def test_malformed_review_keeps_structured_cli_refusal(self):
        discovered, _ = self.invoke("additive")
        review = self.review_for(discovered)
        review["dispositions"][0]["reviewer"] = None
        refused, output = self.invoke("additive", review=review)
        self.assertEqual(refused["status"], "failed")
        self.assertIn("dispositions[0].reviewer must be a string", refused["error"])
        cli_log = (self.output / f"case-{self.counter:02d}.log").read_text()
        self.assertIn(str((output / "result.json").resolve()), cli_log)
        self.assertNotIn("Traceback", cli_log)

    def test_malformed_review_shapes_are_descriptive(self):
        cases = [(None, "Review must be a JSON object"), ([], "Review must be a JSON object"),
                 ({"candidate": []}, "Review candidate must be a JSON object"),
                 ({"baseline": 3}, "Review baseline must be a JSON object or null"),
                 ({"dispositions": None}, "Review dispositions must be a JSON array"),
                 ({"dispositions": {}}, "Review dispositions must be a JSON array"),
                 ({"dispositions": [None]}, "Review dispositions[0] must be a JSON object")]
        valid_row = {"key": "finding", "decision": "compatible-addition", "reviewer": "Reviewer", "rationale": "Reviewed"}
        for field in ("key", "decision", "reviewer", "rationale"):
            for invalid in (2, []):
                cases.append(({"dispositions": [{**valid_row, field: invalid}]}, f"dispositions[0].{field} must be a string"))
        for invalid in (None, 2, []):
            cases.append(({"migration_notes": invalid}, "Review migration_notes must be a string"))
        for review, expected in cases:
            with self.subTest(review=review):
                with self.assertRaises(ValueError) as error:
                    preflight.validate_review(review)
                self.assertIn(expected, str(error.exception))

    def test_real_minver_refuses_metadata_and_competing_tag(self):
        copied = self.output / "metadata"
        shutil.copytree(self.artifacts["compatible"], copied)
        manifest = preflight.read_json(copied / "manifest.json")
        manifest["version"] += "+build"
        # A metadata-bearing request cannot identify MinVer's normalized package version.
        folder = self.output / "metadata-minver"
        folder.mkdir()
        with self.assertRaisesRegex(ValueError, "does not equal the real MinVer"):
            preflight.minver(self.repo, manifest["commit"], manifest["version"], folder, "Fixture.csproj")
        competing = self.output / "competing-source"
        self.command(["git", "clone", "--no-hardlinks", self.repo, competing])
        self.command(["git", "-C", competing, "tag", "v0.9.0-rc.1", self.commits["compatible"]])
        result, _ = self.invoke("compatible", repo=competing)
        self.assertIn("does not equal the real MinVer", result["error"])

    def test_semver_order_channels_and_published_collisions(self):
        def policy(version, *, named=(), published=()):
            history = {"named": [{"version": value, "commit": str(index + 1) * 40} for index, value in enumerate(named)],
                       "published": [{"id": "Preflight.Fixture", "version": value} for value in published]}
            return preflight.progression(version, history, self.output)

        ordered = ["0.2.0-preview", "0.2.0-preview.2", "0.2.0-preview.10", "0.2.0-preview.Z", "0.2.0-preview.a", "0.2.0-rc", "0.2.0-rc.2", "0.2.0-rc.10"]
        for prior, version in zip(ordered, ordered[1:]):
            self.assertEqual(policy(version, published=[prior])[1]["channel"], "preview" if "preview" in version else "rc")
            with self.assertRaisesRegex(ValueError, "must exceed"):
                policy(prior, published=[version])
        for lower, greater in [("9999999999", "10000000000"), ("2147483647", "2147483648"), ("9" * 5000, "1" + "0" * 5000)]:
            version, prior = "0.2.0-preview." + greater, "0.2.0-preview." + lower
            self.assertEqual(policy(version, published=[prior])[1]["release"][-1], greater)
            with self.assertRaisesRegex(ValueError, "must exceed"):
                policy(prior, published=[version])
        self.assertEqual(policy("0.2.0-preview.-", published=["0.2.0-preview.2147483648"])[1]["release"][-1], "-")
        self.assertEqual(policy("0." + "1" + "0" * 5000 + ".0-preview", published=["0." + "9" * 5000 + ".0-rc"])[1]["major"], "0")
        for versions, expected in [(["0.2.0-preview.a", "0.2.0-preview.Z"], "0.2.0-preview.a"),
                                   (["0.2.0-preview.10000000000", "0.2.0-preview.9999999999"], "0.2.0-preview.10000000000")]:
            self.assertEqual(policy("0.2.0-rc", named=versions)[0]["version"], expected)
        for valid in ("0.2.0-preview", "0.2.0-rc", "0.2.0-preview.any.2.x-y", "0.2.0-rc.-", "0.2.0-preview.01alpha"):
            self.assertIsNone(policy(valid)[0])
        for invalid in ("0.2.0-preview.01", "00.2.0-rc.1", "0.2.0-rc..1", "0.2.0-rc.1\n", "0.2.0-preview.١", "0.2.0+", "0.2.0.0", "v0.2.0-rc"):
            with self.assertRaisesRegex(ValueError, "Invalid SemVer"):
                policy(invalid)
            with self.assertRaisesRegex(ValueError, "Invalid SemVer"):
                policy("0.3.0-rc", published=[invalid])
        with self.assertRaisesRegex(ValueError, "must exceed"):
            policy("0.2.0-rc.10+build", published=["0.2.0-rc.10+other"])
        with self.assertRaisesRegex(ValueError, "already published NuGet identity"):
            policy("0.2.0-preview.a", published=["0.2.0-preview.A"])
        for prior in ("0.1.1-preview.2", "0.1.1-preview.2+other", "0.1.1-preview.10", "0.1.1-rc.1", "0.2.0-preview"):
            history = self.output / "published-history.json"
            preflight.write_json(history, {"named": [], "published": [{"id": "Preflight.Fixture", "version": prior}]})
            result, _ = self.invoke("compatible", history=history, baseline=False)
            self.assertIn("must exceed", result["error"], prior)
        for version in ("0.2.0", "1.0.0-preview.1", "0.2.0-beta.1"):
            folder = self.output / ("rejected-" + version)
            folder.mkdir()
            with self.assertRaisesRegex(ValueError, "0.x preview or rc"):
                preflight.progression(version, {"named": [], "published": []}, folder)

    def test_sdk_reference_failure_cannot_become_success(self):
        folder = self.output / "unresolved"
        result = preflight.run(["dotnet", "msbuild", ROOT / "scripts/releases/preflight/sdk/Compare.proj",
                               "-nologo", "-t:Compare", "-p:Inputs=/missing/preflight.props"],
                              allow_failure=True, log=self.output / "unresolved.log")
        self.assertNotEqual(result.returncode, 0)
        # Run the production comparison against real packages with references deliberately absent.
        with self.assertRaisesRegex(ValueError, "could not resolve/run"):
            preflight.compare(next((self.artifacts["compatible"] / "packages").glob("*.nupkg")),
                              next((self.artifacts["baseline"] / "packages").glob("*.nupkg")), {}, folder, self.guard)
        isolated = self.output / "isolated-runtime"
        isolated.mkdir()
        runtime = next(path for path in self.framework_refs if path.name == "System.Runtime.dll")
        shutil.copyfile(runtime, isolated / runtime.name)
        incomplete = [isolated / runtime.name]
        candidate = next((self.artifacts["compatible"] / "packages").glob("*.nupkg"))
        baseline = next((self.artifacts["baseline"] / "packages").glob("*.nupkg"))
        for name, against, current, previous in [
            ("missing-direct-reference", baseline, incomplete, incomplete),
            ("missing-baseline-reference", baseline, self.framework_refs, incomplete),
            ("missing-first-release-reference", None, incomplete, incomplete),
        ]:
            with self.assertRaisesRegex(ValueError, "could not resolve/run"):
                preflight.compare(candidate, against, {"net10.0": current}, self.output / name,
                                  self.guard, {"net10.0": previous})
            self.assertIn("reference-complete=False", (self.output / name / "reference-guard.log").read_text())
            self.assertFalse((self.output / name / "raw.log").exists(), "Reference failure must precede compatibility decisions")
        json_ref = next(path for path in self.framework_refs if path.name == "System.Text.Json.dll")
        shutil.copyfile(json_ref, isolated / json_ref.name)
        incomplete.append(isolated / json_ref.name)
        with self.assertRaisesRegex(ValueError, "could not resolve/run"):
            preflight.compare(candidate, baseline, {"net10.0": incomplete}, self.output / "missing-transitive-reference", self.guard)

    def test_all_managed_asset_groups_are_validated(self):
        original = next((self.artifacts["compatible"] / "packages").glob("*.nupkg"))
        with zipfile.ZipFile(original) as archive:
            dll = archive.read("lib/net10.0/Fixture.dll")
        supported = self.output / "supported-groups.nupkg"
        shutil.copyfile(original, supported)
        with zipfile.ZipFile(supported, "a") as archive:
            archive.writestr("ref/net10.0/Fixture.dll", dll)
            archive.writestr("runtimes/osx-arm64/lib/net10.0/Fixture.dll", dll)
        details, _ = preflight.compare(supported, None, {"net10.0": self.framework_refs}, self.output / "supported-groups", self.guard)
        self.assertEqual(details, [])
        unsupported = self.output / "unsupported-groups.nupkg"
        shutil.copyfile(original, unsupported)
        with zipfile.ZipFile(unsupported, "a") as archive:
            archive.writestr("lib/net9.0/Fixture.dll", dll)
        with self.assertRaisesRegex(ValueError, "Unsupported managed asset group"):
            preflight.compare(unsupported, None, {"net10.0": self.framework_refs}, self.output / "unsupported-groups", self.guard)

    @classmethod
    def tearDownClass(cls):
        after = preflight.snapshot(cls.repo)
        preflight.write_json(cls.output / "source-before.json", cls.before)
        preflight.write_json(cls.output / "source-after.json", after)
        if after != cls.before:
            raise AssertionError("The production preflight mutated source repository refs/status.")


if __name__ == "__main__":
    unittest.main(verbosity=2)
