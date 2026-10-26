"""The real .githooks/pre-commit, run by `git commit` in a throwaway repository with its own identity and no machine
hooks: a docs site page staged in part is refused, since check-docs.py reads the working tree, not the commit."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
# The hook calls `$(dirname "$0")/../.agents/scripts/` and check-docs.py takes its root as `parents[2]`, so the fixture
# keeps both at this repository's depths.
COPIED = (".githooks/pre-commit", ".githooks/staged-conflict-markers", ".agents/scripts/check-tree.py", ".agents/scripts/check-docs.py")
# The machine's own git configuration (hooks, identity, a trace2 target that writes notes after a commit) stays out.
ENV = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")} | {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
}
PAGE = "website/content/docs/guides/rename.mdx"
INVALID = "# Rename\n\nCall `Product.Retitle()`.\n"  # a name no code declares, which check-docs.py fails
CORRECTED = "# Rename\n\nCall `Product.Rename()`.\n"
PARTIAL = "pre-commit: staged in part, so the checks would read the working tree, not the commit"


class PreCommitTests(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory(prefix="pre-commit-")
        self.addCleanup(temp.cleanup)
        self.repo = Path(temp.name)
        self.git("init", "--quiet")
        for key, value in (("user.name", "Hook Test"), ("user.email", "hook-test@example.invalid"), ("core.hooksPath", os.devnull)):
            self.git("config", key, value)
        for path in COPIED:
            (self.repo / path).parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / path, self.repo / path)
        self.write("src/Catalog/Product.cs", "public sealed class Product { public void Rename() { } }\n")
        self.git("add", "-A")
        self.git("commit", "--quiet", "-m", "base")
        self.git("config", "core.hooksPath", str(self.repo / ".githooks"))
        self.base = self.head()

    def git(self, *args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["git", *args], cwd=self.repo, env=ENV, capture_output=True, text=True, check=check)

    def write(self, path: str, text: str) -> None:
        (self.repo / path).parent.mkdir(parents=True, exist_ok=True)
        (self.repo / path).write_text(text, encoding="utf-8")

    def head(self) -> str:
        return self.git("rev-parse", "HEAD").stdout.strip()

    def commit(self) -> subprocess.CompletedProcess[str]:
        return self.git("commit", "--quiet", "-m", "the page", check=False)

    def test_a_site_page_staged_invalid_with_its_correction_unstaged_is_refused_and_staged_whole_commits(self) -> None:
        self.write(PAGE, INVALID)
        self.git("add", PAGE)
        self.write(PAGE, CORRECTED)
        refused = self.commit()
        self.assertIn(f"{PARTIAL}; stage the whole file or stash the rest:\n  {PAGE}\n", refused.stderr)
        self.assertNotEqual(0, refused.returncode)
        self.assertEqual(self.base, self.head())
        self.git("add", PAGE)
        done = self.commit()
        self.assertNotIn(PARTIAL, done.stderr)
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual(CORRECTED, self.git("show", f"HEAD:{PAGE}").stdout)

    def test_a_site_page_staged_whole_with_an_unknown_name_is_refused_by_the_docs_check(self) -> None:
        self.write(PAGE, INVALID)
        self.git("add", PAGE)
        refused = self.commit()
        self.assertNotIn(PARTIAL, refused.stderr)
        self.assertIn("pre-commit: check-docs.py --staged failed", refused.stderr)
        self.assertIn(f"FAIL  {PAGE}:3: `Retitle` names nothing in the code", refused.stdout + refused.stderr)  # git sends a hook's output to either
        self.assertEqual(self.base, self.head())


if __name__ == "__main__":
    unittest.main()
