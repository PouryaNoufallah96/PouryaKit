"""check-docs.py on a small repository: a clean tree passes, and each planted defect fails with its own line."""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-docs.py")
MEMORY = ".claude/agent-memory/kit-implementer/MEMORY.md"
# The paragraph as 2c3b617 fixed it, and as 6ecf342 left it: a sentence inserted inside the span
# `PouryaKit.Messaging.Wolverine`, which pairs every later backtick of the paragraph with the wrong partner.
FIXED = (
    "The second is the delivery step of `PouryaKit.Messaging.Wolverine`, which restores the context from the Outbox"
    " headers the Kit wrote, through `OperationContext.RestoreTrusted`. The third is `PouryaKit.Web`'s trusted-peer"
    " binding: `OperationContextHttpHeaders.Continued` restores a trusted peer's context from its headers through"
    " `OperationContext.RestoreTrusted`, after `HttpCallerContext` verified the authenticated peer (the final structural"
    " review's R1, 2026-10-08). That member is internal.\n"
)
SPLIT = (
    "The second is the delivery step of `PouryaKit.Messaging. The third is `PouryaKit.Web`'s trusted-peer binding:"
    " `OperationContextHttpHeaders.Continued` restores a trusted peer's context from its headers through"
    " `OperationContext.RestoreTrusted`, after `HttpCallerContext` verified the authenticated peer (the final structural"
    " review's R1, 2026-10-08).Wolverine`, which restores the context from the Outbox headers the Kit wrote, through"
    " `OperationContext.RestoreTrusted`. That member is internal.\n"
)
TABLE = "| Name | Kind |\n|---|---|\n| `Product` | class |\n"
CLEAN = {
    "src/Catalog/Product.cs": "public sealed class Product { public void Rename() { } }\n",
    # Not the Kit's, and not drift: a namespace root, an allowed name, a header and prose.
    "README.md": "# Shop\n\n`Product.Rename()` renames a `Product`. `System.Byte[]`, `AllowAnyOrigin`,"
    " `WWW-Authenticate: Bearer` and `My Shop` are not the Kit's.\nSee [the guide](docs/guide.md#the-product).\n",
    "docs/guide.md": "# Guide\n\n## The product\n\n" + TABLE,
    "docs/adr/0007-context.md": "# Context\n\n" + FIXED,
    MEMORY: "- [Product facts](product-facts.md) — the product\n- [Order facts](order-facts.md) — orders\n",
    ".claude/agent-memory/kit-implementer/product-facts.md": "# Product\n",
    ".claude/agent-memory/kit-implementer/order-facts.md": "# Order\n",
    ".gitattributes": f"{MEMORY} merge=union\n",
}


def git(root: Path, *args: str) -> None:
    subprocess.run(["git", "-c", "core.hooksPath=/dev/null", *args], cwd=root, capture_output=True, check=True)


class CheckDocsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="check-docs-"))
        self.addCleanup(shutil.rmtree, self.root)
        self.write(CLEAN)
        (self.root / ".agents/scripts").mkdir(parents=True)
        shutil.copy(SCRIPT, self.root / ".agents/scripts/check-docs.py")
        git(self.root, "init", "-q")

    def write(self, files: dict[str, str]) -> None:
        for path, text in files.items():
            (self.root / path).parent.mkdir(parents=True, exist_ok=True)
            (self.root / path).write_text(text, encoding="utf-8")

    def check(self, *args: str) -> tuple[int, str]:
        git(self.root, "add", "-A")
        done = subprocess.run([sys.executable, ".agents/scripts/check-docs.py", *args], cwd=self.root, capture_output=True, text=True)
        return done.returncode, done.stdout

    def assertFails(self, files: dict[str, str], line: str) -> None:
        self.write(files)
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn(f"FAIL  {line}", out)

    def test_a_clean_tree_passes(self) -> None:
        self.assertEqual(self.check(), (0, "check-docs: 0 failure(s) in 6 Markdown file(s)\n"))

    def test_a_renamed_kit_symbol_fails(self) -> None:
        self.assertFails({"docs/guide.md": "# Guide\n\nCall `Product.Retitle()`.\n"}, "docs/guide.md:3: `Retitle` names nothing in the code")

    def test_a_link_to_a_missing_file_fails(self) -> None:
        self.assertFails({"docs/guide.md": "# Guide\n\nSee [the shop](../READ-ME.md).\n"}, "docs/guide.md:3: link to ../READ-ME.md: no such file")

    def test_a_link_to_a_missing_anchor_fails(self) -> None:
        self.assertFails({"README.md": "# Shop\n\nSee [the guide](docs/guide.md#the-products).\n"}, "README.md:3: link to docs/guide.md#the-products: no heading of docs/guide.md has the anchor")

    def test_a_sentence_inserted_inside_a_code_span_fails(self) -> None:
        self.assertFails({"docs/adr/0007-context.md": "# Context\n\n" + SPLIT}, "docs/adr/0007-context.md:3: code span `PouryaKit.Messaging. The third is ` starts or ends with a space")

    def test_a_row_cut_off_from_its_table_fails(self) -> None:
        orphan = "# Guide\n\n## The product\n\n" + TABLE + "\n## Orders\n\nA paragraph.\n| `Product.Rename()` | method |\n"
        self.assertFails({"docs/guide.md": orphan}, "docs/guide.md:12: a table row with no header and separator line above it")

    def test_a_line_a_union_merge_kept_twice_fails(self) -> None:
        twice = CLEAN[MEMORY] + "- [Product facts](product-facts.md) — the product\n"
        self.assertFails({MEMORY: twice}, f"{MEMORY}:3: repeats line 1")

    def test_two_index_lines_linking_one_file_fail(self) -> None:
        both = CLEAN[MEMORY] + "- [Product facts](product-facts.md) — the product, renamed\n"
        self.assertFails({MEMORY: both}, f"{MEMORY}:3: links product-facts.md, as line 1 does")

    def test_fences_names_a_file_whose_fenced_block_changed(self) -> None:
        self.write({"docs/guide.md": "# Guide\n\n```bash\ndotnet build\n```\n"})
        git(self.root, "add", "-A")
        git(self.root, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "base")
        self.write({"docs/guide.md": "# Guide\n\n```bash\ndotnet test\n```\n", "README.md": CLEAN["README.md"] + "More prose.\n"})
        code, out = self.check("--fences")
        self.assertEqual(code, 0, out)
        self.assertIn("CHANGED docs/guide.md: 1 -> 1 fenced blocks ['bash']", out)
        self.assertIn("same    README.md: 0 -> 0 fenced blocks []", out)


if __name__ == "__main__":
    unittest.main()
