"""resolve-acceptance.py on the conflict two Wave tickets make in docs/acceptance.md, made by git in a throwaway
repository: resolved in ticket order in the default style, refused in the diff3 styles, which hold the base's text."""

from __future__ import annotations

import importlib.util
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().with_name("resolve-acceptance.py")
_spec = importlib.util.spec_from_file_location("resolve_acceptance", SCRIPT)
resolve_acceptance = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(resolve_acceptance)

# The machine's own git configuration (hooks, signing, a daemon that writes notes after a commit) stays out.
ISOLATED = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}
BASE = "# Acceptance\n\n## Wave 4 first, ticket #170\n\nRow 170.\n\n## Wave 4 last, ticket #173\n\nRow 173.\n"
HEADING = re.compile(r"(?m)^## Wave 4 .*, ticket #(\d+)$")
MARKER = re.compile(r"(?m)^(?:<{7}|\|{7}|={7}|>{7})(?: |$)")


def inserted(section: str) -> str:
    """BASE with `section` inserted before #173's, where both tickets insert theirs."""
    return BASE.replace("## Wave 4 last", section + "## Wave 4 last")


class ResolveAcceptanceTests(unittest.TestCase):
    def conflicted(self, style: str) -> Path:
        """docs/acceptance.md once #172's branch merges into one that inserted #171 at the same place, in `style`."""
        temp = tempfile.TemporaryDirectory(prefix="resolve-acceptance-")
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        path = root / "docs" / "acceptance.md"
        path.parent.mkdir()

        def git(*arguments: str, check: bool = True) -> None:
            subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "-c", "user.name=Proof", "-c", "user.email=proof@example.invalid",
                            "-c", f"merge.conflictStyle={style}", *arguments], cwd=root, env=ISOLATED, capture_output=True, check=check)

        git("init", "-q", "-b", "wave")
        path.write_text(BASE, encoding="utf-8")
        git("add", "-A")
        git("commit", "-q", "-m", "base")
        git("checkout", "-q", "-b", "ticket-172")
        path.write_text(inserted("## Wave 4 third, ticket #172\n\nRow 172.\n\n"), encoding="utf-8")
        git("commit", "-q", "-am", "#172")
        git("checkout", "-q", "wave")
        path.write_text(inserted("## Wave 4 second, ticket #171\n\nRow 171.\n\n"), encoding="utf-8")
        git("commit", "-q", "-am", "#171")
        git("merge", "-q", "ticket-172", check=False)
        self.assertRegex(path.read_text(encoding="utf-8"), MARKER)
        return path

    def run_script(self, path: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run([sys.executable, SCRIPT, path], capture_output=True, text=True)

    def test_an_insertion_conflict_in_the_default_style_keeps_both_sections_in_ticket_order(self):
        path = self.conflicted("merge")

        done = self.run_script(path)

        text = path.read_text(encoding="utf-8")
        self.assertEqual((0, "sections: [171, 172]\n"), (done.returncode, done.stdout))
        self.assertEqual(["170", "171", "172", "173"], HEADING.findall(text))
        self.assertEqual([], MARKER.findall(text))
        for row in ("Row 170.", "Row 171.", "Row 172.", "Row 173."):
            self.assertEqual(1, text.count(row), row)

    def test_a_block_in_a_diff3_style_is_refused_and_the_file_left_as_it_was(self):
        for style in ("diff3", "zdiff3"):
            with self.subTest(style=style):
                path = self.conflicted(style)
                before = path.read_text(encoding="utf-8")

                done = self.run_script(path)

                self.assertEqual(1, done.returncode)
                self.assertIn("merge.conflictStyle is diff3 or zdiff3", done.stderr)
                self.assertEqual(before, path.read_text(encoding="utf-8"))
                # The fix the refusal names: the conflict written again in the default style resolves.
                subprocess.run(["git", "checkout", "--conflict=merge", "--", "docs/acceptance.md"], cwd=path.parent.parent, env=ISOLATED,
                               capture_output=True, check=True)
                again = self.run_script(path)
                self.assertEqual((0, "sections: [171, 172]\n"), (again.returncode, again.stdout))

    def test_a_base_section_that_holds_text_is_refused_not_kept_as_a_side(self):
        block = ("<<<<<<< HEAD\n## Wave 4 second, ticket #171\n\nRow 171.\n"
                 "||||||| base\n## Wave 4 first, ticket #170\n\nRow 170, as it was.\n"
                 "=======\n## Wave 4 third, ticket #172\n\nRow 172.\n>>>>>>> ticket-172\n")

        with self.assertRaises(SystemExit) as refused:
            resolve_acceptance.resolve(BASE.replace("## Wave 4 last", block + "## Wave 4 last"))

        self.assertIn("merge.conflictStyle is diff3 or zdiff3", str(refused.exception.code))


if __name__ == "__main__":
    unittest.main()
