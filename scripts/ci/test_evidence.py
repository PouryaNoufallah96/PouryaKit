"""CI evidence boundaries at real Git and filesystem seams."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import evidence


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.output = self.root / "evidence"
        self.output.mkdir()
        self.identity = {"tested_sha": "a" * 40, "repository": "owner/repo", "event": "push",
                         "ref": "refs/heads/main", "run_id": "17", "run_attempt": "2",
                         "shallow": False, "tags": [], "pull_request": None}
        (self.output / "revision.json").write_text(json.dumps(self.identity))

    def package_set(self):
        package = self.output / "packages/PouryaKit.Domain.0.1.0-preview.1.nupkg"
        package.parent.mkdir()
        package.write_bytes(b"validated package")
        manifest = {"commit": self.identity["tested_sha"], "version": "0.1.0-preview.1",
                    "packages": [{"id": "PouryaKit.Domain", "file": package.name,
                                  "sha256": hashlib.sha256(package.read_bytes()).hexdigest()}]}
        (self.output / "manifest.json").write_text(json.dumps(manifest))
        return package, manifest

    def test_success_keeps_packages_logs_assets_and_excludes_credentials_and_caches(self):
        package, manifest = self.package_set()
        (self.output / "build.log").write_text("Release build passed")
        assets = self.output / "consumers/Domain/obj/project.assets.json"
        assets.parent.mkdir(parents=True)
        assets.write_text('{"libraries": {"PouryaKit.Domain/0.1.0-preview.1": {"type": "package"}}}')
        for path in ("caches/Domain/packages/unrelated.nupkg", "caches/Domain/cli/credential.json",
                     "consumers/Domain/NuGet.Config", "controls/corrupt/package.nupkg", "token.json"):
            file = self.output / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("must not upload")
        evidence.collect(self.output, "success")
        artifact = self.output / "artifact"
        self.assertEqual(package.read_bytes(), (artifact / "packages" / package.name).read_bytes())
        self.assertEqual("Release build passed", (artifact / "build.log").read_text())
        self.assertEqual(assets.read_bytes(), (artifact / assets.relative_to(self.output)).read_bytes())
        receipt = json.loads((artifact / "validation.json").read_text())
        self.assertEqual(("success", manifest["commit"], manifest["version"]),
                         (receipt["validation_result"], receipt["tested_sha"], receipt["version"]))
        self.assertEqual({"revision.json", "manifest.json", "validation.json", "build.log",
                          "packages/" + package.name, "consumers/Domain/obj/project.assets.json"},
                         {str(file.relative_to(artifact)) for file in artifact.rglob("*") if file.is_file()})

    def test_failure_has_diagnostics_but_no_successful_manifest_or_version(self):
        self.package_set()
        (self.output / "consumer-Domain-build.log").write_text("error CS0246")
        evidence.collect(self.output, "failure")
        receipt = json.loads((self.output / "artifact/validation.json").read_text())
        self.assertEqual("failure", receipt["validation_result"])
        self.assertIsNone(receipt["version"])
        self.assertIsNone(receipt["manifest"])
        self.assertEqual("error CS0246", (self.output / "artifact/consumer-Domain-build.log").read_text())

    def test_success_rejects_changed_sha_and_package_bytes(self):
        package, manifest = self.package_set()
        path = self.output / "manifest.json"
        path.write_text(json.dumps({**manifest, "commit": "b" * 40}))
        with self.assertRaisesRegex(ValueError, "tested checkout"):
            evidence.collect(self.output, "success")
        path.write_text(json.dumps(manifest))
        package.write_bytes(b"substitute package")
        with self.assertRaisesRegex(ValueError, "checksum"):
            evidence.collect(self.output, "success")
        self.assertFalse((self.output / "artifact").exists())

    def test_evidence_cannot_copy_symlinks_outside_the_directory(self):
        secret = self.root / "credential"
        secret.write_text("must not upload")
        (self.output / "build.log").symlink_to(secret)
        with self.assertRaisesRegex(ValueError, "escapes"):
            evidence.collect(self.output, "failure")
        self.assertFalse((self.output / "artifact/build.log").exists())

    def test_revision_records_merge_sha_and_distinct_head_and_base_and_rejects_wrong_checkout(self):
        checkout = self.root / "checkout"
        checkout.mkdir()
        subprocess.run(["git", "init", "-q", str(checkout)], check=True)
        subprocess.run(["git", "-C", str(checkout), "-c", "user.name=Proof", "-c", "user.email=proof@example.invalid",
                        "commit", "-q", "--allow-empty", "-m", "fixture"], check=True)
        commit = subprocess.check_output(["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True).strip()
        subprocess.run(["git", "-C", str(checkout), "tag", "v0.1.0-preview.1"], check=True)
        event = self.root / "event.json"
        event.write_text(json.dumps({"number": 3, "pull_request": {
            "head": {"sha": "b" * 40, "repo": {"full_name": "fork/repo"}}, "base": {"sha": "c" * 40}}}))
        env = {"GITHUB_SHA": commit, "GITHUB_EVENT_PATH": str(event), "GITHUB_REPOSITORY": "owner/repo",
               "GITHUB_EVENT_NAME": "pull_request", "GITHUB_REF": "refs/pull/3/merge",
               "GITHUB_RUN_ID": "17", "GITHUB_RUN_ATTEMPT": "2"}
        with patch.object(evidence, "ROOT", checkout), patch.dict(os.environ, env):
            evidence.revision(self.output)
            receipt = json.loads((self.output / "revision.json").read_text())
            self.assertEqual(commit, receipt["tested_sha"])
            self.assertEqual(("b" * 40, "c" * 40),
                             (receipt["pull_request"]["head_sha"], receipt["pull_request"]["base_sha"]))
            self.assertEqual(["v0.1.0-preview.1"], receipt["tags"])
            with patch.dict(os.environ, {"GITHUB_SHA": "d" * 40}):
                with self.assertRaisesRegex(ValueError, "intended revision"):
                    evidence.revision(self.output)


if __name__ == "__main__":
    unittest.main()
