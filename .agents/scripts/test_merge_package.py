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


def _load_script():
    import importlib.util

    spec = importlib.util.spec_from_file_location("merge_package", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


merge_package = _load_script()
WAVE = "feat/wave"  # the throwaway repository's integration branch
# The machine's own git configuration (hooks, signing, identity) stays out of the throwaway repositories.
ENV = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")} | {
    "GIT_CONFIG_GLOBAL": os.devnull,
    "GIT_CONFIG_NOSYSTEM": "1",
    "MERGE_PACKAGE_TESTS": "1",
    "MERGE_PACKAGE_READBACK_SECONDS": "0",
}


class MergePackageTest(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory(prefix="merge-package-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.repo, self.origin = self.root / "repo", self.root / "origin.git"
        self.git_in(self.root, "init", "--quiet", "--bare", "-b", WAVE, str(self.origin))
        self.git_in(self.root, "init", "--quiet", "-b", WAVE, str(self.repo))
        for key, value in (("user.name", "Merge Test"), ("user.email", "merge-test@example.invalid"), ("core.hooksPath", os.devnull)):
            self.git("config", key, value)
        self.base = self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n", "docs: base")
        self.git("remote", "add", "origin", str(self.origin))
        self.git("push", "--quiet", "origin", WAVE)
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
        """Branch `pkg`, one commit ahead of the integration branch, which stays checked out."""
        self.git("checkout", "--quiet", "-b", "pkg")
        head = self.commit(name, text, "feat: the package")
        self.git("checkout", "--quiet", WAVE)
        return head

    def commit_after_the_report(self) -> None:
        self.git("checkout", "--quiet", "pkg")
        self.commit("late.txt", "late\n", "fix: committed after the report")
        self.git("checkout", "--quiet", WAVE)

    def report(self, head: str, branch: str = "pkg", validated: str | None = "head", validate: str | None = None) -> str:
        """A report as save-report.py writes it: a header, prose with a brace, then the JSON with a nested object last.
        `validated` names the commit validate.py ran on (the head by default); `validate` is the prose line instead."""
        body = {"status": "done", "branch": branch, "head": head[:7], "commands": [{"command": "verify", "outcome": "passed"}]}
        if validated == "head":
            body["validated"] = head[:7]
        elif validated:
            body["validated"] = validated[:7]
        if validate:
            body["validate"] = validate
        path = self.root / "report-pkg.md"
        path.write_text(f"# pkg report\n\nThe {{ base }} came first.\n\n```json\n{json.dumps(body, indent=2)}\n```\n")
        return str(path)

    def check(self, name: str, body: str) -> str:
        path = self.root / name
        path.write_text(body + "\n")
        return shlex.join([sys.executable, str(path)])

    def merge(self, *args: str, checks: list[str] | None = None, branch: str | None = WAVE, env: dict[str, str] = ENV) -> subprocess.CompletedProcess[str]:
        """Runs the script from a subdirectory of the checkout, as the orchestrator may."""
        argv = [sys.executable, str(SCRIPT), *args, *(["--branch", branch] if branch else []), *(["--checks", *checks] if checks else [])]
        return subprocess.run(argv, cwd=self.repo / "docs", env=env, capture_output=True, text=True)

    def summary(self, done: subprocess.CompletedProcess[str]) -> str:
        return done.stdout.strip().splitlines()[-1]

    def origin_tip(self, branch: str = WAVE) -> str:
        return self.git_in(self.origin, "rev-parse", branch)

    def main_at_the_tip(self) -> None:
        """Origin gets a `main` at the integration branch's tip, which then holds the real tree check and a .gitignore."""
        self.commit(".agents/scripts/check-tree.py", SCRIPT.with_name("check-tree.py").read_text(), "chore: the tree check")
        self.commit(".gitignore", "bin/\n", "chore: ignore build output")
        self.git("push", "--quiet", "origin", WAVE, f"{WAVE}:main")

    def move_base(self, files: dict[str, str]) -> None:
        """Another clone commits to origin's main, so this repository learns of the commit only by fetching."""
        other = self.root / "other"
        self.git_in(self.root, "-c", "core.hooksPath=" + os.devnull, "clone", "--quiet", "--branch", "main", str(self.origin), str(other))
        for name, text in files.items():
            (other / name).parent.mkdir(parents=True, exist_ok=True)
            (other / name).write_text(text)
        self.git_in(other, "add", "--force", "--all")
        self.git_in(other, "-c", "user.name=Other", "-c", "user.email=other@example.invalid", "-c", "core.hooksPath=" + os.devnull, "commit", "--quiet", "-m", "chore: main moved")
        self.git_in(other, "push", "--quiet", "origin", "main")

    def assert_nothing_changed(self, head: str) -> None:
        """--ready merged, committed and pushed nothing."""
        self.assertEqual((self.git("rev-parse", "HEAD"), self.git("status", "--porcelain")), (head, ""))
        self.assertFalse((self.repo / ".git/MERGE_HEAD").exists())
        self.assertEqual(self.origin_tip(), head)

    def test_refuses_a_dirty_checkout(self) -> None:
        head = self.package()
        (self.repo / "stray.txt").write_text("stray\n")
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 4, done.stdout + done.stderr)
        self.assertIn("stray.txt", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_a_branch_ahead_of_the_reported_head(self) -> None:
        head = self.package()
        self.commit_after_the_report()
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 7, done.stdout + done.stderr)
        self.assertIn("fix: committed after the report", done.stderr)
        self.assertIn("the agent committed after reporting: merge its tip or ask it", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_a_revision_expression_as_the_branch(self) -> None:
        """`pkg~1` resolves to the stale head once the agent committed again, but it names no branch."""
        head = self.package()
        self.commit_after_the_report()
        done = self.merge(self.report(head, branch="pkg~1"), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 6, done.stdout + done.stderr)
        self.assertIn("'pkg~1' is not a branch name", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_a_wrong_or_missing_integration_branch(self) -> None:
        head = self.package()
        self.git("checkout", "--quiet", "-b", "other")
        missing = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing], branch=None)
        self.assertEqual(missing.returncode, 2, missing.stdout + missing.stderr)
        self.assertIn("--branch", missing.stderr)
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 8, done.stdout + done.stderr)
        self.assertIn(f"the current branch is other, not the integration branch {WAVE}", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)
        self.assertEqual(self.git("symbolic-ref", "--short", "HEAD"), "other")
        self.assertEqual(self.origin_tip(), self.base)

    def test_refuses_main_as_the_integration_branch(self) -> None:
        head = self.package()
        self.git("checkout", "--quiet", "-b", "main")
        self.git("push", "--quiet", "origin", "main")
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing], branch="main")
        self.assertEqual(done.returncode, 8, done.stdout + done.stderr)
        self.assertIn("main is never the integration branch", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)
        self.assertEqual(self.origin_tip("main"), self.base)

    def test_the_checks_override_needs_the_test_environment(self) -> None:
        head = self.package()
        outside = {key: value for key, value in ENV.items() if key != "MERGE_PACKAGE_TESTS"}
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing], env=outside)
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("MERGE_PACKAGE_TESTS=1", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)
        self.assertEqual(self.origin_tip(), self.base)

    def test_refuses_a_head_with_code_after_the_validation(self) -> None:
        # The shape of ticket #77's fix round (2026-10-09): validate.py ran on 7086ce8, then 7baa118 added a .cs file.
        validated = self.package()
        self.git("checkout", "--quiet", "pkg")
        head = self.commit("tests/StrayType.cs", "class StrayType;\n", "test: added after the validation")
        self.git("checkout", "--quiet", WAVE)
        done = self.merge(self.report(head, validated=validated), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 14, done.stdout + done.stderr)
        self.assertIn("holds code the validation on", done.stderr)
        self.assertIn("  tests/StrayType.cs", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_code_renamed_to_markdown_after_the_validation(self) -> None:
        # Rename detection names a renamed file by its new path alone, so a .cs file moved to .md read as Markdown only
        # (cubic on PR #103).
        validated = self.package("src/Moved.cs", "class Moved;\n")
        self.git("checkout", "--quiet", "pkg")
        self.git("mv", "src/Moved.cs", "src/Moved.md")
        self.git("commit", "--quiet", "-m", "docs: renamed after the validation")
        head = self.git("rev-parse", "HEAD")
        self.git("checkout", "--quiet", WAVE)
        done = self.merge(self.report(head, validated=validated), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 14, done.stdout + done.stderr)
        self.assertIn("  src/Moved.cs", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_accepts_markdown_committed_after_the_validation(self) -> None:
        validated = self.package()
        self.git("checkout", "--quiet", "pkg")
        head = self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## pkg\n\nPASS\n", "docs: the PASS line")
        self.git("checkout", "--quiet", WAVE)
        done = self.merge(self.report(head, validated=validated), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn(f"validated={validated[:7]}: only Markdown differs", done.stdout)

    def test_reads_the_validated_commit_from_the_validate_line(self) -> None:
        head = self.package()
        line = f"PASS 21 packages. On commit {head[:7]}, version 0.1.0-preview.0.23"
        done = self.merge(self.report(head, validated=None, validate=line), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn(f"validated={head[:7]}", done.stdout)

    def test_a_report_whose_json_does_not_parse_names_the_broken_line(self) -> None:
        """Session 5, #87: a fix-round report's `fixes` entry lacked a key, the outer object failed to parse, and the scan
        read a nested `commands` entry as the report. The message names the broken object's line instead."""
        head = self.package()
        path = Path(self.report(head))
        text = path.read_text().replace('"commands"', '"fixes": {"R1": "done", "R2: not a key"},\n  "commands"')
        path.write_text(text)
        done = self.merge(str(path), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 3, done.stdout + done.stderr)
        self.assertIn("does not parse, so a nested object was read instead", done.stderr)
        self.assertRegex(done.stderr, r"the object at line \d+: ")
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_refuses_a_report_that_names_no_validated_commit(self) -> None:
        head = self.package()
        done = self.merge(self.report(head, validated=None), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 3, done.stdout + done.stderr)
        self.assertIn("names no commit the package validation ran on", done.stderr)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.base)

    def test_merges_and_reports_an_equal_tree(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        merged = self.git("rev-parse", "HEAD")
        self.assertEqual(self.git("rev-parse", "HEAD^1", "HEAD^2").splitlines(), [self.base, head])
        self.assertEqual(self.git("log", "-1", "--format=%s"), "Merge pkg")
        self.assertEqual(self.summary(done), f"merge={merged[:7]} tree=EQUAL checks=ok push=no")
        self.assertEqual(self.origin_tip(), self.base)

    def test_reports_a_differing_tree_and_does_not_push_it(self) -> None:
        head = self.package()
        self.commit("other.txt", "other\n", "docs: the integration branch moved")
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 12, done.stdout + done.stderr)
        self.assertIn("  other.txt", done.stdout)
        self.assertIn("The ticket's verify must run on the merged tree", done.stdout)
        self.assertEqual(self.summary(done), f"merge={self.git('rev-parse', '--short=7', 'HEAD')} tree=DIFFERS(1) checks=ok push=no")
        self.assertEqual(self.origin_tip(), self.base)

    def test_stops_on_a_conflict_naming_the_file(self) -> None:
        head = self.package("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## #2\n")
        self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## #3\n", "docs: another package's section")
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 10, done.stdout + done.stderr)
        self.assertIn("  docs/acceptance.md", done.stderr)
        self.assertIn("keep both sections, in ticket order", done.stderr)
        self.assertEqual(self.git("rev-parse", "MERGE_HEAD"), head)
        self.assertEqual(self.origin_tip(), self.base)
        # The orchestrator resolves and commits the merge, then runs the post-merge form on the resolved tree.
        (self.repo / "docs/acceptance.md").write_text("# Acceptance\n\n## #1\n\n## #2\n\n## #3\n")
        self.git("commit", "--quiet", "--all", "--no-edit")
        done = self.merge("--post-merge", head[:7], "--push", checks=[self.passing])
        self.assertEqual(done.returncode, 12, done.stdout + done.stderr)
        self.assertTrue(self.summary(done).endswith(" tree=DIFFERS(1) checks=ok push=no"), done.stdout)
        self.assertEqual(self.origin_tip(), self.base)

    def test_a_failing_check_stops_the_push(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing, self.failing])
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("FAIL x: broken", done.stdout)
        self.assertTrue(self.summary(done).endswith(" tree=EQUAL checks=FAIL(failing) push=no"), done.stdout)
        self.assertEqual(self.origin_tip(), self.base)

    def test_a_missing_check_script_fails(self) -> None:
        """Without --checks the script runs the checkout's own check scripts, and this throwaway checkout has neither."""
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("check-docs.py is missing", done.stdout)
        self.assertTrue(self.summary(done).endswith(" checks=FAIL(check-ai-setup,check-docs) push=no"), done.stdout)
        self.assertEqual(self.origin_tip(), self.base)

    def test_pushes_only_when_told_and_clean(self) -> None:
        head = self.package()
        done = self.merge(self.report(head), "-m", "Merge pkg", checks=[self.passing])
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.origin_tip(), self.base)
        done = self.merge("--post-merge", head[:7], "--push", checks=[self.passing])
        merged = self.git("rev-parse", "HEAD")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.summary(done), f"merge={merged[:7]} tree=EQUAL checks=ok push=yes remote=ok")
        self.assertEqual(self.origin_tip(), merged)

    def test_a_pushed_tip_replaced_on_the_remote_is_reported_and_fails(self) -> None:
        """An actor holding the credential rewrites the tip right after the push (2026-10-25): the read-back catches it."""
        head = self.package()
        self.git("push", "--quiet", "origin", f"{head}:refs/heads/attacker")
        hook = self.origin / "hooks" / "post-receive"
        hook.write_text(f"#!/bin/sh\ngit update-ref refs/heads/{WAVE} {head}\n")
        hook.chmod(0o755)
        done = self.merge(self.report(head), "-m", "Merge pkg", "--push", checks=[self.passing])
        merged = self.git("rev-parse", "HEAD")
        self.assertEqual(done.returncode, 15, done.stdout + done.stderr)
        self.assertEqual(self.summary(done), f"merge={merged[:7]} tree=EQUAL checks=ok push=yes remote=REPLACED({head[:7]})")
        self.assertIn("was replaced after the push", done.stdout)
        self.assertEqual(self.origin_tip(), head)

    def test_ready_when_the_base_holds_nothing_the_branch_lacks(self) -> None:
        self.main_at_the_tip()
        head = self.commit("docs/guide.md", "# Guide\n", "docs: the branch is ahead of main")
        self.git("push", "--quiet", "origin", WAVE)
        done = self.merge("--ready")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.summary(done), "ready=yes behind=0 conflicts=0 tree=ok")
        self.assert_nothing_changed(head)

    def test_not_ready_lists_the_commits_of_a_base_that_moved(self) -> None:
        self.main_at_the_tip()
        head = self.git("rev-parse", "HEAD")
        self.move_base({"docs/guide.md": "# Guide\n"})
        done = self.merge("--ready")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("origin/main has 1 commit(s) that HEAD lacks:", done.stdout)
        self.assertIn(" chore: main moved", done.stdout)
        self.assertEqual(self.summary(done), "ready=no behind=1 conflicts=0 tree=ok")
        self.assert_nothing_changed(head)

    def test_not_ready_names_a_conflict_and_a_path_the_tree_check_refuses(self) -> None:
        """Pull request #65's base: another project's files, and a file both sides changed."""
        self.main_at_the_tip()
        head = self.commit("docs/acceptance.md", "# Acceptance\n\n## #1\n\n## #2\n", "docs: a package's section")
        self.git("push", "--quiet", "origin", WAVE)
        self.move_base({"docs/acceptance.md": "# Acceptance\n\n## #1\n\n## #3\n", ".idea/workspace.xml": "<project />\n"})
        done = self.merge("--ready")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("would conflict in 1 path(s):\n  docs/acceptance.md\n", done.stdout)
        self.assertIn("  .idea/workspace.xml: '.idea' is not a top-level entry of this repository\n", done.stdout)
        self.assertEqual(self.summary(done), "ready=no behind=1 conflicts=1 tree=FAIL(1)")
        self.assert_nothing_changed(head)

    def test_not_ready_judges_the_merged_gitignore_not_the_checkouts(self) -> None:
        self.main_at_the_tip()
        head = self.git("rev-parse", "HEAD")
        self.move_base({".gitignore": "bin/\ndocs/\n"})
        done = self.merge("--ready")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("  .gitignore:2: 'docs/' ignores the tracked path docs/acceptance.md\n", done.stdout)
        self.assertEqual(self.summary(done), "ready=no behind=1 conflicts=0 tree=FAIL(1)")
        self.assert_nothing_changed(head)

    def test_not_ready_fails_the_tree_when_the_tree_check_is_missing(self) -> None:
        self.git("push", "--quiet", "origin", f"{WAVE}:main")
        self.move_base({"docs/guide.md": "# Guide\n"})
        done = self.merge("--ready")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("check-tree.py is missing", done.stdout)
        self.assertEqual(self.summary(done), "ready=no behind=1 conflicts=0 tree=FAIL(1)")

    def test_ready_takes_no_merge_arguments(self) -> None:
        done = self.merge("--ready", "--push")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("--ready takes --branch and --base only", done.stderr)


class HelperTests(unittest.TestCase):
    def test_diagram_pairs_counts_a_source_and_its_page_once(self) -> None:
        conflicted = [
            "docs/acceptance.md",
            "docs/architecture/diagrams/execution-core.architecture.json",
            "docs/architecture/diagrams/execution-core.html",
            "docs/architecture/diagrams/kit-context.architecture.json",
        ]
        self.assertEqual(["execution-core", "kit-context"], merge_package.diagram_pairs(conflicted))


if __name__ == "__main__":
    unittest.main()
