"""Tests for merge-package.py, each in a throwaway repository with its own identity, no hooks and a bare origin."""

from __future__ import annotations

import json
import os
import shlex
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().with_name("merge-package.py")
# The machine's own git configuration (hooks, signing, identity) stays out of the throwaway repositories.
ENV = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")} | {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
}


class MergePackageTest(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory(prefix="merge-package-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.repo, self.origin = self.root / "repo", self.root / "origin.git"
        self.git_in(self.root, "init", "--quiet", "--bare", "-b", "main", str(self.origin))
        self.git_in(self.root, "init", "--quiet", "-b", "main", str(self.repo))
        for key, value in (("user.name", "Merge Test"), ("user.email", "merge-test@example.invalid"), ("core.hooksPath", os.devnull)):
            self.git("config", key, value)
        self.base = self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n", "docs: base")
        self.git("remote", "add", "origin", str(self.origin))
        self.git("push", "--quiet", "origin", "main")
        # Check scripts sit outside the repository, so they never make its checkout dirty.
        self.passing = self.check("passing.py", "print('passing: 0 failure(s)')")
        self.failing = self.check("failing.py", "print('FAIL x: broken')\nraise SystemExit(1)")

    def git_in(self, cwd: Path, *args: str) -> str:
        return subprocess.run(["git", *args], cwd=cwd, env=ENV, capture_output=True, text=True, check=True).stdout.strip()

    def git(self, *args: str) -> str:
        return self.git_in(self.repo, *args)

    def commit(self, name: str, text: str, message: str) -> str:
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)
        self.git("add", name)
        self.git("commit", "--quiet", "-m", message)
        return self.git("rev-parse", "HEAD")

    def package(self, name: str = "feature.txt", text: str = "feature\n") -> str:
        """Branch `pkg`, one commit ahead of main; main stays checked out."""
        self.git("checkout", "--quiet", "-b", "pkg")
        head = self.commit(name, text, "feat: the package")
        self.git("checkout", "--quiet", "main")
        return head

    def report(self, head: str) -> str:
        """A report as save-report.py writes it: a header, prose with a brace, then the JSON with a nested object last."""
        body = {"status": "done", "branch": "pkg", "head": head[:7], "commands": [{"command": "verify", "outcome": "passed"}]}
        path = self.root / "report-pkg.md"
        path.write_text(f"# pkg report\n\nThe {{ base }} came first.\n\n```json\n{json.dumps(body, indent=2)}\n```\n")
        return str(path)

    def check(self, name: str, body: str) -> str:
        path = self.root / name
        path.write_text(body + "\n")
        return shlex.join([sys.executable, str(path)])

    def merge(self, *args: str, checks: list[str] | None = None) -> subprocess.CompletedProcess[str]:
        """Runs the script from a subdirectory of the checkout, as the orchestrator may."""
        argv = [sys.executable, str(SCRIPT), *args, *(["--checks", *checks] if checks else [])]
        return subprocess.run(argv, cwd=self.repo / "docs", env=ENV, capture_output=True, text=True)

    def summary(self, done: subprocess.CompletedProcess[str]) -> str:
        return done.stdout.strip().splitlines()[-1]

    def origin_main(self) -> str:
        return self.git_in(self.origin, "rev-parse", "main")

    def test_refuses_a_dirty_checkout(self) -> None:
        head = self.package()
        (self.repo / "stray.txt").write_text("stray\n")
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 4, done.stdout + done.stderr)
        self.assertIn("stray.txt", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_a_branch_ahead_of_the_reported_head(self) -> None:
        head = self.package()
        self.git("checkout", "--quiet", "pkg")
        self.commit("late.txt", "late\n", "fix: committed after the report")
        self.git("checkout", "--quiet", "main")
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 7, done.stdout + done.stderr)
        self.assertIn("fix: committed after the report", done.stderr)
        self.assertIn("the agent committed after reporting: merge its tip or ask it", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_merges_and_reports_an_equal_tree(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        merged = self.git("rev-parse", "HEAD")
        self.assertEqual(self.git("rev-parse", "HEAD^1", "HEAD^2").splitlines(), [self.base, head])
        self.assertEqual(self.git("log", "-1", "--format=%s"), "Merge pkg")
        self.assertEqual(self.summary(done), f"merge={merged[:7]} tree=EQUAL checks=ok push=no")
        self.assertEqual(self.origin_main(), self.base)

    def test_reports_a_differing_tree_and_does_not_push_it(self) -> None:
        head = self.package()
        self.commit("other.txt", "other\n", "docs: the integration branch moved")
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 12, done.stdout + done.stderr)
        self.assertIn("  other.txt", done.stdout)
        self.assertIn("The ticket's verify must run on the merged tree", done.stdout)
        self.assertEqual(self.summary(done), f"merge={self.git('rev-parse', '--short=7', 'HEAD')} tree=DIFFERS(1) checks=ok push=no")
        self.assertEqual(self.origin_main(), self.base)

    def test_stops_on_a_conflict_naming_the_file(self) -> None:
        head = self.package("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## #2\n")
        self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## #3\n", "docs: another package's section")
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 10, done.stdout + done.stderr)
        self.assertIn("  docs/acceptance.md", done.stderr)
        self.assertIn("keep both sections, in ticket order", done.stderr)
        self.assertEqual(self.git("rev-parse", "MERGE_HEAD"), head)
        self.assertEqual(self.origin_main(), self.base)
        # The orchestrator resolves and commits the merge, then runs the post-merge form on the resolved tree.
        (self.repo / "docs/acceptance.md").write_text("# Acceptance\n\n## #1\n\n## #2\n\n## #3\n")
        self.git("commit", "--quiet", "--all", "--no-edit")
        done = self.merge("--post-merge", head[:7], "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 12, done.stdout + done.stderr)
        self.assertTrue(self.summary(done).endswith(" tree=DIFFERS(1) checks=ok push=no"), done.stdout)
        self.assertEqual(self.origin_main(), self.base)

    def test_a_failing_check_stops_the_push(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing, self.failing])
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("FAIL x: broken", done.stdout)
        self.assertTrue(self.summary(done).endswith(" tree=EQUAL checks=FAIL(failing) push=no"), done.stdout)
        self.assertEqual(self.origin_main(), self.base)

    def test_a_missing_check_script_fails(self) -> None:
        """Without --checks the script runs the checkout's own check scripts, and this throwaway checkout has neither."""
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("check-docs.py is missing", done.stdout)
        self.assertTrue(self.summary(done).endswith(" checks=FAIL(check-ai-setup,check-docs) push=no"), done.stdout)
        self.assertEqual(self.origin_main(), self.base)

    def test_pushes_only_when_told_and_clean(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.origin_main(), self.base)
        done = self.merge("--post-merge", head[:7], "--push", checks=[self.passing])
        merged = self.git("rev-parse", "HEAD")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.summary(done), f"merge={merged[:7]} tree=EQUAL checks=ok push=yes")
        self.assertEqual(self.origin_main(), merged)


if __name__ == "__main__":
    unittest.main()
