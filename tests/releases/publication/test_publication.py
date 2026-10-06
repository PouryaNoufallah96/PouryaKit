"""Real HTTP protocol and original archive bytes. No writes to GitHub or a production feed."""

import argparse
import copy
from datetime import datetime, timedelta, timezone
from email.parser import BytesParser
from email.policy import default
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import urllib.parse
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/releases/publication"))
import artifacts
import publisher
import run
import transport
from transport import API, FEED, OWNER, REPOSITORY, Http, PublicationError


def encoded(value):
    return json.dumps(value).encode()


def identity(content):
    with zipfile.ZipFile(io.BytesIO(content)) as archive:
        spec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
        node = ET.fromstring(archive.read(spec))
    values = {element.tag.split("}")[-1]: element.text for element in node.iter()}
    return values["id"].lower(), values["version"].lower()


class Protocol:
    def __init__(self, candidate):
        self.candidate = candidate
        self.manifest = json.loads((candidate / "manifest.json").read_text())
        self.sha = self.manifest["commit"]
        self.run = {"id": 51, "run_attempt": 2, "event": "push", "head_branch": "main", "head_sha": self.sha,
                    "path": ".github/workflows/ci.yml", "repository": {"full_name": REPOSITORY},
                    "head_repository": {"full_name": REPOSITORY}, "status": "completed", "conclusion": "failure"}
        self.jobs = [{"name": name, "status": "completed", "conclusion": "success"}
                     for name in ("ai-setup", "build-and-test / validate", "publication-protocol")]
        self.remote = {}
        self.events = []
        self.auth = []
        self.failures = {}
        self.visibility = "private"
        self.change_upload_bytes = False
        self.change_earlier_on_last = False
        self.redirect = None
        self.extra_packages = 0
        self.extra_artifacts = 0
        self.archive = self.bundle()
        self.artifact = {"id": 800, "name": "package-validation-51-2", "expired": False,
                         "expires_at": (datetime.now(timezone.utc) + timedelta(days=90)).isoformat(),
                         "digest": "sha256:" + hashlib.sha256(self.archive).hexdigest(),
                         "workflow_run": {"id": 51, "head_sha": self.sha, "head_branch": "main"}}
        protocol = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                protocol.auth.append((self.path, self.headers.get("Authorization")))
                status, body, headers = protocol.get(self.path)
                self.send_response(status)
                for key, value in headers.items():
                    self.send_header(key, value)
                self.end_headers()
                self.wfile.write(body)

            def do_PUT(self):
                protocol.auth.append((self.path, self.headers.get("Authorization")))
                if self.path in protocol.failures:
                    self.send_response(protocol.failures[self.path])
                    self.end_headers()
                    return
                self.server.received_key = self.headers.get("X-NuGet-ApiKey")
                body = self.rfile.read(int(self.headers["Content-Length"]))
                message = BytesParser(policy=default).parsebytes(
                    ("Content-Type: " + self.headers["Content-Type"] + "\r\n\r\n").encode() + body)
                parts = list(message.iter_parts())
                content = parts[0].get_payload(decode=True)
                package_id, version = identity(content)
                protocol.events.append(("PUT", package_id))
                if (package_id, version) in protocol.remote:
                    self.send_response(409)
                else:
                    protocol.remote[package_id, version] = content + (b"rewritten-by-feed" if protocol.change_upload_bytes else b"")
                    if protocol.change_earlier_on_last and len(protocol.remote) == 13:
                        first = next(iter(protocol.remote))
                        protocol.remote[first] += b"changed-after-initial-verification"
                    self.send_response(201)
                self.end_headers()

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def bundle(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in self.candidate.rglob("*"):
                if path.is_file():
                    archive.write(path, path.relative_to(self.candidate))
        return buffer.getvalue()

    def metadata(self, name):
        return {"id": 7, "name": name, "package_type": "nuget", "visibility": self.visibility,
                "repository": {"full_name": REPOSITORY}}

    def page(self, path, entries, key=None):
        parsed = urllib.parse.urlsplit(path)
        page = int(urllib.parse.parse_qs(parsed.query).get("page", ["1"])[0])
        subset = entries[(page - 1) * 100:page * 100]
        headers = {}
        if len(entries) > page * 100:
            headers["Link"] = f'<{API}{parsed.path}?per_page=100&page={page + 1}>; rel="next"'
        return 200, encoded({key: subset} if key else subset), headers

    def get(self, path):
        route = urllib.parse.urlsplit(path).path
        if route in self.failures:
            return self.failures[route], b"private upstream response must never be retained", {}
        prefix = f"/repos/{REPOSITORY}"
        if route == prefix:
            return 200, encoded({"full_name": REPOSITORY, "private": True, "default_branch": "main"}), {}
        if route == prefix + "/actions/runs/51/attempts/2":
            return 200, encoded(self.run), {}
        if route == prefix + "/actions/runs/51/attempts/2/jobs":
            return self.page(path, self.jobs, "jobs")
        if route == prefix + "/actions/runs/51/artifacts":
            entries = [{"id": index + 1000, "name": f"unrelated-{index}"} for index in range(self.extra_artifacts)]
            return self.page(path, [*entries, self.artifact], "artifacts")
        if route == prefix + "/actions/artifacts/800":
            return 200, encoded(self.artifact), {}
        if route == prefix + "/actions/artifacts/800/zip":
            if self.redirect:
                return 302, b"", {"Location": self.redirect}
            return 200, self.archive, {}
        if route == "/storage/original.zip":
            return 200, self.archive, {}
        if route == f"/{OWNER}/index.json":
            return 200, encoded({"version": "3.0.0", "resources": [
                {"@type": "PackageBaseAddress/3.0.0", "@id": f"https://nuget.pkg.github.com/{OWNER}/original-content/"},
                {"@type": "PackagePublish/2.0.0", "@id": f"https://nuget.pkg.github.com/{OWNER}/upload"}]}), {}
        if route == f"/users/{OWNER}/packages":
            entries = [self.metadata(f"unrelated.{index}") for index in range(self.extra_packages)]
            entries += [self.metadata(name) for name in sorted({name for name, version in self.remote})]
            return self.page(path, entries)
        if route.startswith(f"/users/{OWNER}/packages/nuget/"):
            name = route.rsplit("/", 1)[1]
            if any(package_id == name for package_id, version in self.remote):
                return 200, encoded(self.metadata(name)), {}
            return 404, b"", {}
        if route.startswith(f"/{OWNER}/original-content/"):
            rest = route.split("/original-content/")[1].split("/")
            if len(rest) == 2 and rest[1] == "index.json":
                self.events.append(("INDEX", rest[0]))
                versions = [version for package_id, version in self.remote if package_id == rest[0]]
                return (200, encoded({"versions": versions}), {}) if versions else (404, b"", {})
            self.events.append(("GET", rest[0]))
            content = self.remote.get((rest[0], rest[1]))
            return (200, content, {}) if content is not None else (404, b"", {})
        return 404, b"", {}

    def client(self, token="test-token"):
        origin = f"http://127.0.0.1:{self.server.server_port}"

        class LocalHttp(Http):
            def checked_url(self, url, auth):
                if url.startswith(origin + "/"):
                    return
                super().checked_url(url, auth)

            def request(self, method, url, **kwargs):
                super().checked_url(url, kwargs.get("auth", "api"))
                parsed = urllib.parse.urlsplit(url)
                target = origin + parsed.path + ("?" + parsed.query if parsed.query else "")
                return super().request(method, target, **kwargs)

        return LocalHttp(token)


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.candidate = self.root / "candidate"
        self.candidate.mkdir()
        fixture = os.environ.get("POURYAKIT_PUBLICATION_FIXTURE")
        if fixture:
            original = Path(fixture)
            shutil.copyfile(original / "manifest.json", self.candidate / "manifest.json")
            shutil.copyfile(original / "package-evidence.json", self.candidate / "package-evidence.json")
            shutil.copytree(original / "packages", self.candidate / "packages")
        else:
            self.synthetic()
        self.manifest = json.loads((self.candidate / "manifest.json").read_text())
        self.sha = self.manifest["commit"]
        revision = {"repository": REPOSITORY, "event": "push", "ref": "refs/heads/main", "tested_sha": self.sha,
                    "run_id": "51", "run_attempt": "2", "shallow": False, "tags": ["spike/fixture"], "pull_request": None}
        validation = {**revision, "validation_result": "success", "version": self.manifest["version"],
                      "manifest": "manifest.json", "artifact_name": "package-validation-51-2"}
        history = {"mode": "checkout", "shallow": False, "commit": self.sha, "tags": revision["tags"]}
        for name, content in (("revision", revision), ("validation", validation), ("history", history)):
            (self.candidate / f"{name}.json").write_bytes(encoded(content))
        self.protocol = Protocol(self.candidate)
        self.addCleanup(self.protocol.close)
        self.receipt = self.root / "receipt.json"
        self.arguments = argparse.Namespace(run_id="51", attempt="2", artifact_id="800", source_sha=self.sha,
                                            proof="none", receipt=self.receipt)

    def synthetic(self):
        sha, version = "a" * 40, "0.1.0-preview.0.12"
        (self.candidate / "packages").mkdir()
        packages = []
        inventory = json.loads((ROOT / "scripts/packaging/inventory.json").read_text())["packages"]
        for item in inventory:
            package_id = item["id"]
            file = self.candidate / "packages" / f"{package_id}.{version}.nupkg"
            with zipfile.ZipFile(file, "w") as archive:
                archive.writestr(f"{package_id}.nuspec", f'<package><metadata><id>{package_id}</id><version>{version}</version>'
                                 f'<repository url="https://github.com/{REPOSITORY}" commit="{sha}" /></metadata></package>')
                archive.writestr("lib/net10.0/fixture.dll", b"binary fixture\x00\xff")
            packages.append({"id": package_id, "file": file.name, "sha256": hashlib.sha256(file.read_bytes()).hexdigest()})
        (self.candidate / "manifest.json").write_bytes(encoded({"commit": sha, "version": version, "packages": packages}))
        (self.candidate / "package-evidence.json").write_bytes(encoded({"Version": version, "Commit": sha}))

    def execute(self, proof="none", token="test-token"):
        self.arguments.proof = proof
        with patch.dict(os.environ, {"PUBLICATION_TOKEN": token}):
            return run.execute(self.arguments, client_factory=self.protocol.client)

    def receipt_document(self):
        return json.loads(self.receipt.read_text())

    def seed(self, index):
        package = self.manifest["packages"][index]
        content = (self.candidate / "packages" / package["file"]).read_bytes()
        self.protocol.remote[package["id"].lower(), self.manifest["version"]] = content

    def test_empty_feed_publishes_exact_raw_bytes_and_identical_retry_uploads_nothing(self):
        self.assertEqual(0, self.execute())
        result = self.receipt_document()
        self.assertTrue(result["complete"])
        self.assertEqual(13, len(result["uploaded"]))
        self.assertEqual(13, len(result["verified"]))
        for package in self.manifest["packages"]:
            self.assertEqual((self.candidate / "packages" / package["file"]).read_bytes(),
                             self.protocol.remote[package["id"].lower(), self.manifest["version"]])
        self.assertEqual("test-token", self.protocol.server.received_key)
        self.assertEqual(0, self.execute())
        self.assertEqual([], self.receipt_document()["uploaded"])
        self.assertEqual(13, len([event for event in self.protocol.events if event[0] == "PUT"]))
        self.assertNotIn("test-token", self.receipt.read_text())

    def test_partial_failure_after_one_confirmed_upload_resumes_missing_only(self):
        self.assertEqual(1, self.execute("fail-after-one"))
        result = self.receipt_document()
        self.assertFalse(result["complete"])
        self.assertEqual((1, 1, 1), (len(result["uploaded"]), len(result["confirmed"]), len(self.protocol.remote)))
        self.assertEqual(0, self.execute())
        self.assertEqual(12, len(self.receipt_document()["uploaded"]))
        self.assertEqual(13, len(self.receipt_document()["verified"]))

    def test_all_existing_checked_before_missing_write_and_late_byte_conflict_rejected(self):
        self.seed(-1)
        package = self.manifest["packages"][-1]
        key = (package["id"].lower(), self.manifest["version"])
        self.protocol.remote[key] += b"conflicting bytes"
        self.assertEqual(1, self.execute())
        self.assertIn("conflict", self.receipt_document()["error"])
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))

    def test_existing_source_conflict_rejected_before_any_missing_write(self):
        self.seed(-1)
        package = self.manifest["packages"][-1]
        key = (package["id"].lower(), self.manifest["version"])
        buffer = io.BytesIO()
        with zipfile.ZipFile(io.BytesIO(self.protocol.remote[key])) as original, zipfile.ZipFile(buffer, "w") as changed:
            for item in original.infolist():
                content = original.read(item)
                if item.filename.endswith(".nuspec"):
                    content = content.replace(self.sha.encode(), b"b" * 40)
                changed.writestr(item, content)
        self.protocol.remote[key] = buffer.getvalue()
        self.assertEqual(1, self.execute())
        self.assertIn("source SHA", self.receipt_document()["error"])
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))

    def test_raw_bytes_changed_by_feed_stop_after_first_upload(self):
        self.protocol.change_upload_bytes = True
        self.assertEqual(1, self.execute())
        self.assertEqual(1, len(self.protocol.remote))
        self.assertFalse(self.receipt_document()["complete"])

    def test_full_set_rechecked_after_last_upload(self):
        self.protocol.change_earlier_on_last = True
        self.assertEqual(1, self.execute())
        self.assertEqual(13, len(self.protocol.remote))
        self.assertFalse(self.receipt_document()["complete"])

    def test_read_only_probe_retains_observed_history_without_uploads(self):
        self.seed(0)
        self.assertEqual(0, self.execute("probe"))
        result = self.receipt_document()
        self.assertEqual("probe-success", result["result"])
        self.assertFalse(result["complete"])
        self.assertEqual(13, len(result["versions_before"]))
        self.assertEqual(1, len(result["verified"]))
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))

    def test_private_visibility_and_repository_linkage_are_observed(self):
        for index in range(13):
            self.seed(index)
        self.protocol.visibility = "public"
        self.assertEqual(1, self.execute())
        self.assertIn("observed private", self.receipt_document()["error"])
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))

    def test_original_main_provenance_and_successful_jobs_are_required(self):
        original = copy.deepcopy(self.protocol.run)
        for key, value in (("event", "pull_request"), ("head_branch", "feature"), ("head_sha", "b" * 40),
                           ("run_attempt", 1), ("path", ".github/workflows/untrusted.yml"),
                           ("head_repository", {"full_name": "fork/PouryaKit"})):
            with self.subTest(key=key):
                self.protocol.run = {**original, key: value}
                self.assertEqual(1, self.execute())
                self.assertFalse(self.protocol.remote)
        self.protocol.run = original
        for job in self.protocol.jobs:
            job["conclusion"] = "failure"
            self.assertEqual(1, self.execute())
            self.assertIn("must all have succeeded", self.receipt_document()["error"])
            job["conclusion"] = "success"
        self.protocol.run["status"] = "in_progress"
        self.protocol.run["conclusion"] = None
        self.assertEqual(0, self.execute())

    def test_missing_expired_wrong_artifact_and_archive_digest_fail_without_rebuild(self):
        original = copy.deepcopy(self.protocol.artifact)
        for key, value in (("expired", True), ("digest", "sha256:" + "0" * 64),
                           ("name", "package-validation-51-1"), ("expires_at", "2000-01-01T00:00:00Z"),
                           ("workflow_run", {"id": 52, "head_branch": "main", "head_sha": self.sha})):
            with self.subTest(key=key):
                self.protocol.artifact = {**original, key: value}
                self.assertEqual(1, self.execute())
                self.assertFalse(self.protocol.remote)
        self.protocol.artifact = original
        self.arguments.artifact_id = "999"
        self.assertEqual(1, self.execute())
        self.arguments.artifact_id = "800"
        self.protocol.failures[f"/repos/{REPOSITORY}/actions/artifacts/800/zip"] = 410
        self.assertEqual(1, self.execute())
        self.assertIn("HTTP 410", self.receipt_document()["error"])

    def test_changed_original_content_or_manifest_sha_fail_before_feed_requests(self):
        self.assertEqual(1, self.execute("negative-sha"))
        self.assertFalse(self.protocol.remote)
        self.assertFalse(any(path.startswith(f"/{OWNER}") for path, _ in self.protocol.auth))
        file = self.candidate / "packages" / self.manifest["packages"][0]["file"]
        file.write_bytes(file.read_bytes() + b"changed original")
        self.protocol.archive = self.protocol.bundle()
        self.protocol.artifact["digest"] = "sha256:" + hashlib.sha256(self.protocol.archive).hexdigest()
        self.assertEqual(1, self.execute())
        self.assertFalse(self.protocol.remote)
        self.assertFalse(any(path.startswith(f"/{OWNER}") for path, _ in self.protocol.auth))

    def test_negative_content_copy_checks_real_remote_conflict_without_any_uploads(self):
        self.assertEqual(1, self.execute("negative-content"))
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))
        self.seed(0)
        self.assertEqual(1, self.execute("negative-content"))
        self.assertIn("Remote package bytes conflict", self.receipt_document()["error"])
        self.assertEqual(1, len(self.protocol.remote))
        self.assertFalse(any(event[0] == "PUT" for event in self.protocol.events))

    def test_missing_credentials_and_unauthorized_enumeration_are_not_an_empty_feed(self):
        self.assertEqual(1, self.execute(token=""))
        self.assertIn("requires the hosted repository token", self.receipt_document()["error"])
        self.assertFalse(self.protocol.auth)
        for status in (401, 403):
            self.protocol.failures[f"/users/{OWNER}/packages"] = status
            self.assertEqual(1, self.execute())
            self.assertIn(f"HTTP {status}", self.receipt_document()["error"])
            self.assertFalse(self.protocol.remote)
        self.assertNotIn("private upstream response", self.receipt.read_text())

    def test_pagination_over_100_entries_and_intermediate_failures(self):
        self.protocol.jobs = [{"name": f"unrelated-{index}"} for index in range(100)] + self.protocol.jobs
        self.protocol.extra_artifacts = 100
        self.protocol.extra_packages = 101
        self.assertEqual(0, self.execute())
        self.assertEqual(101, self.receipt_document()["protocol"]["enumerated_count"])
        for route in ("jobs", "artifacts", "packages"):
            self.assertTrue(any("page=2" in path and route in path for path, _ in self.protocol.auth))
        original_get = self.protocol.get

        def failed_second_page(path):
            if "page=2" in path and "/jobs?" in path:
                return 403, b"", {}
            return original_get(path)

        with patch.object(self.protocol, "get", side_effect=failed_second_page):
            self.assertEqual(1, self.execute())

    def test_manifest_inventory_paths_versions_and_missing_original_are_checked(self):
        original = copy.deepcopy(self.manifest)
        for change in ({"packages": original["packages"][:-1]}, {"version": "1.0.0"}, {"commit": "b" * 40},
                       {"packages": [{**original["packages"][0], "file": "../escape.nupkg"}, *original["packages"][1:]]}):
            (self.candidate / "manifest.json").write_bytes(encoded({**original, **change}))
            with self.assertRaises(PublicationError):
                publisher.load_candidate(self.candidate, self.sha)
        (self.candidate / "manifest.json").write_bytes(encoded(original))
        (self.candidate / "packages" / original["packages"][0]["file"]).unlink()
        with self.assertRaisesRegex(PublicationError, "missing"):
            publisher.load_candidate(self.candidate, self.sha)

    def test_unsafe_artifact_extraction_and_pr_receipt_are_rejected(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("../escaped.nupkg", b"must not extract")
        self.protocol.archive = buffer.getvalue()
        self.protocol.artifact["digest"] = "sha256:" + hashlib.sha256(self.protocol.archive).hexdigest()
        self.assertEqual(1, self.execute())
        self.assertIn("unsafe archive path", self.receipt_document()["error"])
        document = json.loads((self.candidate / "validation.json").read_text())
        document["pull_request"] = {"number": 1}
        (self.candidate / "validation.json").write_bytes(encoded(document))
        self.protocol.archive = self.protocol.bundle()
        self.protocol.artifact["digest"] = "sha256:" + hashlib.sha256(self.protocol.archive).hexdigest()
        self.assertEqual(1, self.execute())
        self.assertIn("hosted provenance", self.receipt_document()["error"])

    def test_credential_redirects_and_unfixed_feed_resources_are_rejected(self):
        client = Http("test-token")
        for auth, url in (("feed", "https://api.nuget.org/v3/index.json"),
                          ("feed", "https://nuget.pkg.github.com/other/index.json"),
                          ("api", "https://attacker.example/packages"),
                          ("none", "https://attacker.example/archive"),
                          ("feed", FEED + "?secret=bad")):
            with self.subTest(url=url), self.assertRaises(PublicationError):
                client.checked_url(url, auth)

    def test_artifact_redirect_does_not_forward_credentials_and_rejects_untrusted_host(self):
        self.protocol.redirect = "https://retained.blob.core.windows.net/storage/original.zip"
        self.assertEqual(0, self.execute("probe"))
        self.assertIn(("/storage/original.zip", None), self.protocol.auth)
        self.protocol.redirect = "https://attacker.example/storage/original.zip"
        self.assertEqual(1, self.execute())
        self.assertIn("unsupported host", self.receipt_document()["error"])


class WorkflowTests(unittest.TestCase):
    def test_one_shared_lock_covers_main_recovery_and_future_named_callers(self):
        reusable = (ROOT / ".github/workflows/package-publication.yml").read_text()
        caller = (ROOT / ".github/workflows/private-publication.yml").read_text()
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertEqual(1, reusable.count("group: pouryakit-private-publication"))
        self.assertIn("cancel-in-progress: false", reusable)
        self.assertNotIn("concurrency:", caller)
        self.assertNotIn("uses: ./.github/workflows/package-publication.yml", ci)
        self.assertNotIn("packages: write", ci)
        self.assertNotIn("PUBLICATION_TOKEN:", ci)
        self.assertIn("publication-protocol:", ci)
        self.assertIn("needs: authorize", caller)
        self.assertIn("workflows: [CI]", caller)
        self.assertIn("types: [completed]", caller)
        self.assertIn("branches: [main]", caller)
        self.assertNotIn("workflow_run.conclusion", caller)
        self.assertNotIn("packages: write", caller.split("  publish:")[0])
        self.assertIn("packages: write", reusable)
        self.assertNotIn("contents: write", reusable)
        self.assertNotIn("secrets: inherit", caller)
        self.assertNotIn("persist-credentials: true", reusable + caller)
        self.assertIn("if: ${{ always() }}", reusable)


if __name__ == "__main__":
    unittest.main()
