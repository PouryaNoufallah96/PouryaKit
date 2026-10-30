"""check-tree.py on paths given as arguments: the top-level entries, and the 2026-10-25 incident's path segments refused
at any depth, by segment and not by substring."""

from __future__ import annotations

import subprocess
import sys
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-tree.py")
REFUSED = "is a refused path segment anywhere in this repository (the 2026-10-25 incident's payload paths)"


def check(*paths: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, str(SCRIPT), *paths], capture_output=True, text=True)


class CheckTreeTests(unittest.TestCase):
    def test_a_path_under_the_docs_site_passes_and_a_segment_that_only_contains_a_refused_one_passes(self) -> None:
        done = check("website/app/page.tsx", "website/content/publication.mdx", "website/content/public-api.mdx",
                     "website/content/docs/vscode.mdx", "src/PouryaKit.Web/Publication.cs", "docs/public.md")
        self.assertEqual((0, ""), (done.returncode, done.stdout))

    def test_a_public_or_vscode_segment_is_refused_at_any_depth(self) -> None:
        refused = {
            "website/public/favicon.ico": "public", "website/.vscode/tasks.json": ".vscode", "website/app/public": "public",
            "src/PouryaKit.Web/public/site.js": "public", "public/payload.js": "public", ".vscode/tasks.json": ".vscode",
        }
        done = check(*refused)
        self.assertEqual(1, done.returncode)
        self.assertEqual([f"{path}: '{segment}' {REFUSED}" for path, segment in refused.items()], done.stdout.splitlines())

    def test_a_path_outside_the_top_level_entries_is_refused(self) -> None:
        done = check("websites/page.tsx", "website.json", "website/app/page.tsx")
        self.assertEqual(1, done.returncode)
        self.assertEqual(["websites/page.tsx: 'websites' is not a top-level entry of this repository",
                          "website.json: 'website.json' is not a top-level entry of this repository"], done.stdout.splitlines())


if __name__ == "__main__":
    unittest.main()
