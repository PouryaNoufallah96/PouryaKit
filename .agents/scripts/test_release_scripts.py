"""The release scripts read each commit, version and tag from the checkout, the manifest or the remote. Before they did,
one day's three releases meant re-pinning them by hand four times, and a stale pin stops a release or names the wrong one.
The refusals below run the real script against a bare repository, and need no network, pnpm or Docker."""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ("publish-public.sh", "deploy-site.sh", "publish-nuget.sh")
PIN = re.compile(r"\b[0-9a-f]{40}\b|assets-v\d|\bv?\d+\.\d+\.\d+\b")
ADDRESSES = (ROOT / "website/lib/kit-sources/addresses.ts").read_text(encoding="utf-8")
PINNED_NUMBER = int(re.search(r"ASSETS_TAG = 'assets-v(\d+)'", ADDRESSES).group(1))
PINNED = f"assets-v{PINNED_NUMBER}"
NEXT = f"assets-v{PINNED_NUMBER + 1}"
ENV = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")} | {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
    "GIT_AUTHOR_NAME": "Release Test",
    "GIT_AUTHOR_EMAIL": "release-test@example.invalid",
    "GIT_COMMITTER_NAME": "Release Test",
    "GIT_COMMITTER_EMAIL": "release-test@example.invalid",
}


class PinTests(unittest.TestCase):
    def test_no_release_script_pins_a_commit_a_version_or_a_tag(self) -> None:
        for name in SCRIPTS:
            text = (ROOT / ".agents/scripts" / name).read_text(encoding="utf-8")
            self.assertEqual([], PIN.findall(text), name)


class PublishPublicRefusalTests(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory(prefix="publish-public-")
        self.addCleanup(temp.cleanup)
        self.seed = Path(temp.name) / "seed"
        self.remote = Path(temp.name) / "public.git"
        self.git(None, "init", "--quiet", "--bare", "-b", "main", str(self.remote))
        self.git(None, "init", "--quiet", "-b", "main", str(self.seed))
        (self.seed / "README.md").write_text("released\n", encoding="utf-8")
        self.git(self.seed, "add", "-A")
        self.git(self.seed, "commit", "--quiet", "--no-gpg-sign", "-m", "released")
        self.git(self.seed, "tag", PINNED)
        self.git(self.seed, "push", "--quiet", str(self.remote), "main", PINNED)

    def git(self, cwd: Path | None, *args: str) -> None:
        subprocess.run(["git", *args], cwd=cwd, env=ENV, capture_output=True, text=True, check=True)

    def refs(self) -> str:
        return subprocess.run(["git", "ls-remote", str(self.remote)], env=ENV, capture_output=True, text=True).stdout

    def run_script(self, *args: str) -> subprocess.CompletedProcess[str]:
        before = self.refs()
        command = ["bash", str(ROOT / ".agents/scripts/publish-public.sh"), *args]
        result = subprocess.run(command, env=ENV | {"PUBLIC_REMOTE": str(self.remote)}, capture_output=True, text=True)
        self.assertEqual(before, self.refs(), "a refused run must leave the remote as it found it")
        return result

    def test_a_name_outside_the_tag_series_is_refused(self) -> None:
        refused = self.run_script("release-1")
        self.assertEqual(64, refused.returncode)
        self.assertIn("STOP: usage", refused.stderr)

    def test_a_tag_with_anything_after_its_number_is_refused(self) -> None:
        self.assertEqual(64, self.run_script(f"{NEXT}oops").returncode)

    def test_an_argument_other_than_images_is_refused(self) -> None:
        self.assertEqual(64, self.run_script(NEXT, "--image").returncode)

    def test_a_tag_that_exists_is_refused(self) -> None:
        refused = self.run_script(PINNED)
        self.assertEqual(2, refused.returncode)
        self.assertIn(f"{PINNED} already exists", refused.stderr)

    def test_a_notes_ref_on_the_remote_stops_the_run(self) -> None:
        self.git(self.seed, "push", "--quiet", str(self.remote), "HEAD:refs/notes/ai")
        refused = self.run_script(NEXT)
        self.assertEqual(2, refused.returncode)
        self.assertIn("the remote holds something else", refused.stderr)

    def test_a_remote_without_the_tag_the_readmes_pin_stops_the_run(self) -> None:
        self.git(self.remote, "tag", "-d", PINNED)
        refused = self.run_script(NEXT)
        self.assertEqual(2, refused.returncode)
        self.assertIn(f"has no {PINNED}", refused.stderr)

    def test_new_images_never_move_a_tag(self) -> None:
        refused = self.run_script(NEXT, "--images")
        self.assertEqual(2, refused.returncode)
        self.assertIn(f"{PINNED} already exists and a tag never moves", refused.stderr)

    def test_the_release_and_the_images_need_a_tag_each(self) -> None:
        self.git(self.remote, "tag", "-d", PINNED)
        refused = self.run_script(PINNED, "--images")
        self.assertEqual(2, refused.returncode)
        self.assertIn("need a tag each", refused.stderr)

    def test_a_pinned_tag_short_of_one_image_stops_before_anything_is_pushed(self) -> None:
        inventory = json.loads((ROOT / "scripts/packaging/inventory.json").read_text(encoding="utf-8"))
        ids = [package["id"] for package in inventory["packages"]]
        images = ["assets/icon.png", "assets/kit.png", *(f"assets/covers/{id}.png" for id in ids)]
        missing = images.pop()
        for image in images:
            (self.seed / image).parent.mkdir(parents=True, exist_ok=True)
            (self.seed / image).write_text(image, encoding="utf-8")
        self.git(self.seed, "add", "-A")
        self.git(self.seed, "commit", "--quiet", "--no-gpg-sign", "-m", "images")
        self.git(self.seed, "tag", "--force", PINNED)
        self.git(self.seed, "push", "--quiet", "--force", str(self.remote), "main", PINNED)
        refused = self.run_script(NEXT)
        self.assertEqual(8, refused.returncode)
        self.assertTrue(refused.stderr.endswith(f"nothing was pushed:\n{missing}\n"), refused.stderr)


if __name__ == "__main__":
    unittest.main()
