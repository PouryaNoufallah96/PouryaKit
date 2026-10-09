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
    " `WWW-Authenticate: Bearer` and `My Shop` are not the Kit's.\nSee [the guide](docs/guide.md#the-product) and"
    " [its table][Table].\n\n[table]: docs/guide.md#the-product\n",
    "docs/guide.md": "# Guide\n\n## The product\n\n" + TABLE,
    "docs/adr/0007-context.md": "# Context\n\n" + FIXED,
    MEMORY: "- [Product facts](product-facts.md) — the product\n- [Order facts](order-facts.md) — orders\n",
    ".claude/agent-memory/kit-implementer/product-facts.md": "# Product\n",
    ".claude/agent-memory/kit-implementer/order-facts.md": "# Order\n",
    ".gitattributes": f"{MEMORY} merge=union\n",
}
# A C# test and a Python one, and a record that points at both, names the first one's file and a member that is no test.
TESTS = {
    # Two declared tests, and six names that are no test of ProductTests: one called by an initializer, one called by a
    # test, one in a string, one in a comment, one declared by a nested type and one by another class of the file.
    "tests/Shop.Tests/ProductTests.cs": '''public sealed class ProductTests(Fixture app) : HostTest<Fixture>(app)
{
    private readonly Product _product = Build("{");

    [Fact]
    public void A_product_is_renamed()
    {
        A_product_is_called();
        var text = "void A_product_is_quoted() { }"; // void A_product_is_commented() { }
    }

    [Theory, InlineData(new[] { "a" })]
    public async Task<Result<int>> A_product_is_priced(string[] codes) => await Price(codes);

    private sealed class Stub { public void A_stub_is_declared() { } }
}

public sealed class OtherTests { [Fact] public void An_other_is_declared() { } }
''',
    "scripts/test_shop.py": 'FIXTURE = "class GhostTests:\\n    def test_ghost(self): pass"\n\n\nclass InventoryTests(unittest.TestCase):\n'
    "    def test_inventory_is_exact(self):\n        self.helper_is_called()\n",
    "docs/acceptance.md": "# Acceptance\n\n`ProductTests.A_product_is_renamed`, `ProductTests.A_product_is_priced` and"
    " `InventoryTests.test_inventory_is_exact` pass; see `ProductTests.cs`. `Product.Retitle` is gone.\n",
}
NOT_DECLARED = ("ProductTests.Build", "ProductTests.A_product_is_called", "ProductTests.A_product_is_quoted", "ProductTests.A_product_is_commented",
                "ProductTests.A_stub_is_declared", "ProductTests.An_other_is_declared", "InventoryTests.helper_is_called")
ADOPTED = {"src/Catalog/Money.cs": "// A money value, adapted from the Till project's Money.\npublic readonly record struct Money(decimal Amount);\n"}
RECORD = "# Catalog\n\n## Money\n\n- **Kit files:** `src/Catalog/Money.cs`\n"


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

    def check(self, *args: str, stage: bool = True) -> tuple[int, str]:
        if stage:
            git(self.root, "add", "-A")
        done = subprocess.run([sys.executable, ".agents/scripts/check-docs.py", *args], cwd=self.root, capture_output=True, text=True)
        return done.returncode, done.stdout

    def assertPasses(self, files: dict[str, str]) -> None:
        self.write(files)
        code, out = self.check()
        self.assertEqual(code, 0, out)

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

    def test_a_titled_link_or_a_parenthesised_destination_to_a_missing_file_fails(self) -> None:
        links = "# Guide\n\nSee [a](missing1.md 'Guide'), [b](missing2.md (Guide)) and [c](dir/(x).md).\n"
        self.write({"docs/guide.md": links})
        code, out = self.check()
        self.assertEqual(code, 1, out)
        for target in ("missing1.md", "missing2.md", "dir/(x).md"):
            self.assertIn(f"FAIL  docs/guide.md:3: link to {target}: no such file", out)

    def test_a_percent_encoded_anchor_resolves(self) -> None:
        self.assertPasses({"docs/accents.md": "# Accents\n\n## Café\n\nSee [the café](#caf%C3%A9) and [again](#café).\n"})

    def test_a_reference_definition_to_a_missing_file_fails(self) -> None:
        self.assertFails({"README.md": "# Shop\n\nSee [the shop][shop].\n\n[shop]: READ-ME.md\n"}, "README.md:5: link to READ-ME.md: no such file")

    def test_a_reference_definition_to_a_missing_anchor_fails(self) -> None:
        defined = "# Shop\n\nSee [the guide][guide].\n\n[guide]: docs/guide.md#the-products\n"
        self.assertFails({"README.md": defined}, "README.md:5: link to docs/guide.md#the-products: no heading of docs/guide.md has the anchor")

    def test_a_reference_with_no_definition_fails(self) -> None:
        self.assertFails({"README.md": "# Shop\n\nSee [the shop][nowhere].\n"}, "README.md:3: link to reference [nowhere]: no definition")

    def test_a_link_to_a_tracked_file_deleted_but_not_staged_fails(self) -> None:
        git(self.root, "add", "-A")
        (self.root / "docs/guide.md").unlink()
        code, out = self.check(stage=False)
        self.assertEqual(code, 1, out)
        self.assertIn("FAIL  README.md:4: link to docs/guide.md#the-product: no such file", out)

    def test_a_generated_suffix_and_a_literal_heading_take_distinct_ids(self) -> None:
        steps = "# Guide\n\n## The product\n\n## Step\n\n## Step\n\n## Step-1\n\nThe third is [this one](#step-1-1).\n"
        self.assertPasses({"docs/guide.md": steps})

    def test_a_padded_span_quoting_a_backtick_passes(self) -> None:
        padded = "# Context\n\n" + FIXED + "\nIts name in a padded span: `` `OperationContext.RestoreTrusted` ``.\n"
        self.assertPasses({"docs/adr/0007-context.md": padded})

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

    def test_acceptance_pointers_to_existing_tests_pass(self) -> None:
        self.assertPasses(TESTS)

    def test_an_acceptance_pointer_to_a_renamed_test_or_a_missing_class_fails(self) -> None:
        stale = "# Acceptance\n\n`ProductTests.A_product_is_retitled` and `OrderTests.An_order_is_placed` pass.\n"
        self.write(TESTS | {"docs/acceptance.md": stale})
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn("FAIL  docs/acceptance.md:3: `ProductTests.A_product_is_retitled` names no test: the class ProductTests has no method A_product_is_retitled", out)
        self.assertIn("FAIL  docs/acceptance.md:3: `OrderTests.An_order_is_placed` names no test: no test class OrderTests", out)

    def test_a_name_a_test_file_only_calls_quotes_or_declares_elsewhere_is_no_test(self) -> None:
        pointers = "".join(f"`{pointer}`\n" for pointer in (*NOT_DECLARED, "GhostTests.test_ghost"))
        self.write(TESTS | {"docs/acceptance.md": "# Acceptance\n\n" + pointers})
        code, out = self.check()
        self.assertEqual(code, 1, out)
        for line, pointer in enumerate(NOT_DECLARED, start=3):
            name, method = pointer.split(".")
            self.assertIn(f"FAIL  docs/acceptance.md:{line}: `{pointer}` names no test: the class {name} has no method {method};", out)
        self.assertIn("`GhostTests.test_ghost` names no test: no test class GhostTests;", out)

    def test_adopted_code_without_a_provenance_record_fails(self) -> None:
        """The README's format example names a Kit file too, and is no record."""
        self.assertFails(ADOPTED | {"docs/provenance/README.md": RECORD}, "src/Catalog/Money.cs:1: the comment says the code is adapted from something, and no record in docs/provenance/ names the file under Kit files")

    def test_an_adopted_test_without_a_provenance_record_fails(self) -> None:
        ported = {"tests/Shop.Tests/MoneyTests.cs": "public sealed class MoneyTests\n{\n    // Scenarios ported from the Till project's MoneyTests.\n}\n"}
        self.assertFails(ported, "tests/Shop.Tests/MoneyTests.cs:3: the comment says the code is ported from something, and no record in docs/provenance/ names the file under Kit files")

    def test_adopted_code_a_provenance_record_names_passes_and_so_does_a_string_that_says_the_words(self) -> None:
        quoted = {"src/Catalog/Messages.cs": 'public static class Messages { public const string Source = "taken from the header"; }\n'}
        self.assertPasses(ADOPTED | quoted | {"docs/provenance/Catalog.md": RECORD})

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
