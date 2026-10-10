"""Execute the workflow's trust guard and original-artifact checks without feed access."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


def block(workflow, marker):
    lines = (ROOT / ".github/workflows" / workflow).read_text().splitlines()
    start = next(index for index, line in enumerate(lines) if marker in line) + 1
    selected = []
    for line in lines[start:]:
        if line.strip() and not line.startswith("          "):
            break
        selected.append(line)
    return textwrap.dedent("\n".join(selected))


class PublicationTests(unittest.TestCase):
    def test_only_exact_successful_private_main_ci_and_jobs_are_authorized(self):
        script = block("private-publication.yml", "script: |")
        repository = "PouryaNoufallah96/PouryaKit"
        repo = {"full_name": repository, "private": True, "default_branch": "main"}
        run = {"id": 17, "run_attempt": 2, "status": "completed", "conclusion": "success",
               "event": "push", "head_branch": "main", "head_sha": "a" * 40,
               "path": ".github/workflows/ci.yml", "repository": {"full_name": repository},
               "head_repository": {"full_name": repository}}
        jobs = [{"name": name, "status": "completed", "conclusion": "success"}
                for name in ("ai-setup", "build-and-test / validate")]
        harness = r"""
          const input = JSON.parse(require('fs').readFileSync(0, 'utf8'));
          const assert = require('assert');
          const github = { rest: {
            repos: {get: async () => ({data: input.repo})},
            actions: {getWorkflowRunAttempt: async args => {
              assert.equal(args.run_id, '17'); assert.equal(args.attempt_number, '2');
              return {data: input.run};
            }, listJobsForWorkflowRunAttempt: {}}
          }, paginate: async (method, args) => {
            assert.equal(args.run_id, '17'); assert.equal(args.attempt_number, '2');
            return input.jobs;
          }};
          const context = {repo: {owner: 'PouryaNoufallah96', repo: 'PouryaKit'}};
          const core = {setOutput: () => {}, info: () => {}};
          const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
          new AsyncFunction('github', 'context', 'core', input.script)(github, context, core)
            .catch(error => { console.error(error.message); process.exitCode = 1; });
        """

        def authorize(selected_run=run, selected_repo=repo, selected_jobs=jobs):
            return subprocess.run(["node", "-e", harness], input=json.dumps({
                "repo": selected_repo, "run": selected_run, "jobs": selected_jobs, "script": script}),
                text=True, capture_output=True, env={**os.environ, "ORIGINAL_RUN": "17",
                "ORIGINAL_ATTEMPT": "2", "ORIGINAL_SHA": "a" * 40}).returncode

        for event in ("push", "workflow_dispatch"):
            self.assertEqual(0, authorize({**run, "event": event}))
        for key, value in (("head_branch", "topic"), ("head_sha", "b" * 40), ("event", "pull_request"),
                           ("run_attempt", 1), ("id", 18), ("conclusion", "failure"), ("status", "in_progress"),
                           ("path", ".github/workflows/other.yml"), ("head_repository", {"full_name": "fork/repo"})):
            with self.subTest(key=key):
                self.assertNotEqual(0, authorize({**run, key: value}))
        self.assertNotEqual(0, authorize(selected_repo={**repo, "private": False}))
        self.assertNotEqual(0, authorize(selected_jobs=[]))
        self.assertNotEqual(0, authorize(selected_jobs=[jobs[0], {**jobs[1], "conclusion": "skipped"}]))

    def test_original_artifact_rejects_wrong_provenance_version_inventory_and_changed_bytes(self):
        script = block("package-publication.yml", "        run: |")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "packages").mkdir()
            inventory = json.loads((ROOT / "scripts/packaging/inventory.json").read_text())["packages"]
            version = "0.1.0-preview.0.10"
            manifest = {"commit": "a" * 40, "version": version, "packages": []}
            for package in inventory:
                filename = package["id"] + "." + version + ".nupkg"
                (root / "packages" / filename).write_bytes(package["id"].encode())
                manifest["packages"].append({"id": package["id"], "file": filename,
                    "sha256": hashlib.sha256(package["id"].encode()).hexdigest()})
            receipt = {"repository": "PouryaNoufallah96/PouryaKit", "ref": "refs/heads/main", "event": "push",
                       "tested_sha": "a" * 40, "run_id": "17", "run_attempt": "2", "shallow": False,
                       "pull_request": None, "validation_result": "success", "manifest": "manifest.json",
                       "artifact_name": "package-validation-17-2", "version": version}

            def verify(selected_manifest=manifest, selected_receipt=receipt):
                (root / "manifest.json").write_text(json.dumps(selected_manifest))
                (root / "validation.json").write_text(json.dumps(selected_receipt))
                return subprocess.run(["bash", "-c", script], cwd=ROOT, capture_output=True, text=True,
                    env={**os.environ, "PACKAGES_DIR": folder, "RUNNER_TEMP": folder,
                    "GITHUB_STEP_SUMMARY": str(root / "summary"), "ORIGINAL_RUN": "17", "ORIGINAL_ATTEMPT": "2",
                    "ORIGINAL_SHA": "a" * 40, "ORIGINAL_EVENT": "push"}).returncode

            self.assertEqual(0, verify())
            def set_version(value):
                for package in manifest["packages"]:
                    filename = package["id"] + "." + value + ".nupkg"
                    (root / "packages" / package["file"]).rename(root / "packages" / filename)
                    package["file"] = filename
                manifest["version"] = receipt["version"] = value

            set_version("0.2.0-rc.1")
            self.assertEqual(0, verify())
            set_version("1.0.0")
            self.assertNotEqual(0, verify())
            set_version(version)
            for key, value in (("tested_sha", "b" * 40), ("run_attempt", "1"), ("event", "pull_request"),
                               ("validation_result", "failure")):
                with self.subTest(key=key):
                    self.assertNotEqual(0, verify(selected_receipt={**receipt, key: value}))
            self.assertNotEqual(0, verify({**manifest, "packages": manifest["packages"][:-1]}))
            (root / "packages" / manifest["packages"][-1]["file"]).write_bytes(b"substitute bytes")
            self.assertNotEqual(0, verify())


if __name__ == "__main__":
    unittest.main()
