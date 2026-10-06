#!/usr/bin/env python3
"""Switch off one guarantee in an isolated script copy and require its real protocol test to fail."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
CONTROLS = [
    ("artifacts.py", 'or run.get("event") != "push"', "or False",
     "PublicationTests.test_original_main_provenance_and_successful_jobs_are_required", "trusted push provenance"),
    ("publisher.py", 'if hashlib.sha256(content).hexdigest() != package["sha256"]:', "if False:",
     "PublicationTests.test_changed_original_content_or_manifest_sha_fail_before_feed_requests", "original raw-byte hash"),
    ("publisher.py", 'receipt["verified"].append(verify_remote(feed, package, manifest))', "pass",
     "PublicationTests.test_all_existing_checked_before_missing_write_and_late_byte_conflict_rejected", "check all existing before writes"),
    ("publisher.py", 'or metadata.get("visibility") != "private"', "or False",
     "PublicationTests.test_private_visibility_and_repository_linkage_are_observed", "observed private visibility"),
    ("publisher.py", 'receipt["verified"] = [verify_remote(feed, package, manifest, polling=True) for package in manifest["packages"]]',
     'receipt["verified"] = receipt["confirmed"]',
     "PublicationTests.test_full_set_rechecked_after_last_upload", "final full-set verification"),
    ("package-publication.yml", "cancel-in-progress: false", "cancel-in-progress: true",
     "WorkflowTests.test_one_shared_lock_covers_main_recovery_and_future_named_callers", "active publisher cancellation disabled"),
]


def main():
    for filename, before, after, test, behaviour in CONTROLS:
        with tempfile.TemporaryDirectory(prefix="pouryakit-publication-red-") as folder:
            root = Path(folder)
            for source in ("scripts/releases/publication", "tests/releases/publication", ".github/workflows"):
                shutil.copytree(ROOT / source, root / source, ignore=shutil.ignore_patterns("__pycache__"))
            (root / "scripts/packaging").mkdir()
            shutil.copyfile(ROOT / "scripts/packaging/inventory.json", root / "scripts/packaging/inventory.json")
            target = root / (".github/workflows" if filename.endswith(".yml") else "scripts/releases/publication") / filename
            content = target.read_text()
            if before not in content:
                raise RuntimeError(f"Control no longer matches {filename}")
            target.write_text(content.replace(before, after, 1))
            result = subprocess.run(["python3", "-m", "unittest", "test_publication." + test],
                                    cwd=root / "tests/releases/publication", capture_output=True, text=True)
            if result.returncode == 0 or "FAIL:" not in result.stderr:
                print(result.stdout + result.stderr)
                raise RuntimeError(f"Guarantee control did not show an assertion failure: {test}")
            print(json.dumps({"test": test, "switched_off": behaviour, "outcome": "observed_red"}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
