"""check-docs.py on a small repository: a clean tree passes, and each planted defect fails with its own line."""

from __future__ import annotations

import json
import os
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
    "tests/Shop.Tests/ProductTests.cs": '''namespace Shop.Tests;

public sealed class ProductTests(Fixture app) : HostTest<Fixture>(app)
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
    "tests/Shop.ArchitectureTests/LayerTests.cs": "namespace Shop.ArchitectureTests;\n\npublic sealed class LayerTests { [Fact] public void Layers_hold() { } }\n",
    "scripts/test_shop.py": 'FIXTURE = "class GhostTests:\\n    def test_ghost(self): pass"\n\n\nclass InventoryTests(unittest.TestCase):\n'
    "    def test_inventory_is_exact(self):\n        self.helper_is_called()\n",
    # Qualified by its namespace, a pointer names the method or the class alone; `Shop.ArchitectureTests` names a project.
    "docs/acceptance.md": "# Acceptance\n\n`ProductTests.A_product_is_renamed`, `ProductTests.A_product_is_priced` and"
    " `InventoryTests.test_inventory_is_exact` pass; see `ProductTests.cs`. `Product.Retitle` is gone.\n"
    "`Shop.Tests.ProductTests.A_product_is_priced` and `Shop.ArchitectureTests.LayerTests` pass in `Shop.ArchitectureTests`.\n",
}
NOT_DECLARED = ("ProductTests.Build", "ProductTests.A_product_is_called", "ProductTests.A_product_is_quoted", "ProductTests.A_product_is_commented",
                "ProductTests.A_stub_is_declared", "ProductTests.An_other_is_declared", "InventoryTests.helper_is_called")
ADOPTED = {"src/Catalog/Money.cs": "// A money value, adapted from the Till project's Money.\npublic readonly record struct Money(decimal Amount);\n"}
RECORD = "# Catalog\n\n## Money\n\n- **Kit files:** `src/Catalog/Money.cs`\n"
# A record that cites a Kit file, and an upstream repository's paths under Upstream paths and as `repository@commit path`.
KIT_RECORD = ("# Catalog\n\n## Product\n\n- **Kit files:** `src/Catalog/Product.cs`\n- **Upstream paths:** `src/Till/Product.cs`,"
              " `tests/Till.Tests/ProductTests.cs`\n\nIts rule is the one of `till@abc1234 tests/Till.Tests/RulesTests.cs:3`.\n")
# An Archify diagram source citing a three-line file; `diagram` pins it to a revision with the sources given.
DIAGRAM = "docs/architecture/diagrams/catalog.architecture.json"
CART = {"src/Catalog/Cart.cs": "public sealed class Cart\n{\n}\n"}
REVERIFY = "re-verify the cited ranges against the current code, refinalize the diagram, and set meta.repository.revision to the commit you verified against"


def diagram(revision: str, *sources: dict) -> dict[str, str]:
    source = {"meta": {"repository": {"revision": revision}}, "components": [{"id": "cart", "label": "Cart", "sources": list(sources)}]}
    return {DIAGRAM: json.dumps(source, indent=2)}


# The user's own git configuration stays out of the test repository: a tool it registers, such as a trace2 event target
# that writes notes after each commit, can write into the repository while the cleanup deletes it.
ISOLATED = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}


def git(root: Path, *args: str) -> None:
    subprocess.run(["git", "-c", "core.hooksPath=/dev/null", *args], cwd=root, capture_output=True, check=True, env=ISOLATED)


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

    def commit(self) -> str:
        git(self.root, "add", "-A")
        git(self.root, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "change")
        return subprocess.run(["git", "rev-parse", "HEAD"], cwd=self.root, capture_output=True, text=True, check=True).stdout.strip()

    def test_a_clean_tree_passes(self) -> None:
        self.assertEqual(self.check(), (0, "check-docs: 0 failure(s) in 6 Markdown file(s) and 0 diagram source(s)\n"))

    def test_a_diagram_whose_cited_files_are_unchanged_passes(self) -> None:
        """A range to the file's last line and a single line, while a later commit changed only a file it does not cite."""
        self.write(CART)
        revision = self.commit()
        cited = diagram(revision, {"path": "src/Catalog/Cart.cs", "line": 1, "end_line": 3}, {"path": "src/Catalog/Product.cs", "line": 1})
        self.write(cited | {"README.md": CLEAN["README.md"] + "More prose.\n"})
        self.commit()
        self.assertEqual(self.check(), (0, "check-docs: 0 failure(s) in 6 Markdown file(s) and 1 diagram source(s)\n"))

    def test_a_diagram_citing_a_file_changed_since_its_revision_fails(self) -> None:
        """Outside the cited range too: a range cannot tell a moved line from a rewritten one."""
        self.write(CART)
        revision = self.commit()
        self.write(diagram(revision, {"path": "src/Catalog/Cart.cs", "line": 1, "end_line": 2}) | {"src/Catalog/Cart.cs": CART["src/Catalog/Cart.cs"] + "// Carts.\n"})
        self.commit()
        self.assertFails({}, f"{DIAGRAM}: src/Catalog/Cart.cs:1-2 cites a file changed since the revision {revision[:7]}; {REVERIFY}")

    def test_a_diagram_range_past_the_end_of_its_file_or_out_of_order_fails(self) -> None:
        self.write(CART)
        revision = self.commit()
        self.write(diagram(revision, {"path": "src/Catalog/Cart.cs", "line": 2, "end_line": 4}, {"path": "src/Catalog/Cart.cs", "line": 3, "end_line": 2}))
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn(f"FAIL  {DIAGRAM}: src/Catalog/Cart.cs:2-4 is no ordered range within the file's 3 lines; {REVERIFY}", out)
        self.assertIn(f"FAIL  {DIAGRAM}: src/Catalog/Cart.cs:3-2 is no ordered range within the file's 3 lines; {REVERIFY}", out)

    def test_a_diagram_citing_a_missing_path_fails(self) -> None:
        """A file deleted since the revision, and one that never was."""
        self.write(CART)
        revision = self.commit()
        (self.root / "src/Catalog/Cart.cs").unlink()
        self.write(diagram(revision, {"path": "src/Catalog/Cart.cs", "line": 1}, {"path": "src/Catalog/Till.cs", "line": 1}))
        self.commit()
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn(f"FAIL  {DIAGRAM}: src/Catalog/Cart.cs:1 names no file at HEAD; {REVERIFY}", out)
        self.assertIn(f"FAIL  {DIAGRAM}: src/Catalog/Till.cs:1 names no file at HEAD; {REVERIFY}", out)

    def test_a_diagram_pinned_to_a_revision_unreachable_from_head_fails(self) -> None:
        """A commit the clone lacks, and one it holds that a reset left out of HEAD's history, as a squash or rebase does;
        the cited file is the same in that commit, so only its reachability fails it."""
        self.write(CART)
        kept = self.commit()
        self.write({"README.md": CLEAN["README.md"] + "More prose.\n"})
        squashed = self.commit()
        git(self.root, "reset", "-q", "--hard", kept)
        for revision in ("0123456789abcdef" * 2 + "01234567", squashed):
            with self.subTest(revision=revision):
                self.assertFails(diagram(revision, {"path": "src/Catalog/Cart.cs", "line": 1}), f"{DIAGRAM}: meta.repository.revision {revision} is unreachable here: a shallow clone, or a revision squashed away; {REVERIFY}")

    def test_pre_wave_3_acceptance_sections_out_of_ticket_order_fail(self) -> None:
        """Session 5: #88's section was appended after #97's and left there until an advisor caught it."""
        ordered = "# Acceptance\n\n## Pre-Wave 3 the Web fixes, ticket #86\n\nx\n\n## Pre-Wave 3 naming, ticket #88\n\nx\n\n## Pre-Wave 3 the OpenAPI options, ticket #97\n\nx\n"
        self.assertPasses({"docs/acceptance.md": ordered})
        disordered = "# Acceptance\n\n## Pre-Wave 3 the Web fixes, ticket #86\n\nx\n\n## Pre-Wave 3 the OpenAPI options, ticket #97\n\nx\n\n## Pre-Wave 3 naming, ticket #88\n\nx\n"
        self.assertFails({"docs/acceptance.md": disordered}, "docs/acceptance.md:11: the Pre-Wave 3 section of ticket #88 comes after ticket #97's (line 7); keep these sections in ticket order")
        # Earlier waves' records are not held to it: their sections sit in the order the Phases ran.
        self.assertPasses({"docs/acceptance.md": "# Acceptance\n\n## Wave 2 caching, ticket #43\n\nx\n\n## Wave 2 messaging, ticket #42\n\nx\n"})

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

    def test_a_qualified_pointer_naming_no_declared_test_fails(self) -> None:
        """A method the class lacks, a class of another namespace, and a class no file declares."""
        stale = ("# Acceptance\n\n`Shop.Tests.ProductTests.A_product_is_retitled`, `Shop.Domain.Tests.ProductTests.A_product_is_renamed`"
                 " and `Shop.Tests.OrderTests` pass.\n")
        self.write(TESTS | {"docs/acceptance.md": stale})
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn("FAIL  docs/acceptance.md:3: `Shop.Tests.ProductTests.A_product_is_retitled` names no test: the class Shop.Tests.ProductTests has no method A_product_is_retitled", out)
        self.assertIn("FAIL  docs/acceptance.md:3: `Shop.Domain.Tests.ProductTests.A_product_is_renamed` names no test: no test class Shop.Domain.Tests.ProductTests", out)
        self.assertIn("FAIL  docs/acceptance.md:3: `Shop.Tests.OrderTests` names no test: no test class Shop.Tests.OrderTests", out)

    def test_a_class_pointer_in_a_namespace_that_reads_like_a_test_class_passes(self) -> None:
        """`ArchitectureTests.ModuleTests` also reads as the method ModuleTests of a class ArchitectureTests, which no file
        declares; the class its namespace qualifies is the one meant (cubic on PR #103)."""
        modules = {
            "tests/ArchitectureTests/ModuleTests.cs": "namespace ArchitectureTests;\n\npublic sealed class ModuleTests { [Fact] public void Modules_hold() { } }\n",
            "docs/acceptance.md": TESTS["docs/acceptance.md"] + "`ArchitectureTests.ModuleTests` passes.\n",
        }
        self.assertPasses(TESTS | modules)

    def test_adopted_code_without_a_provenance_record_fails(self) -> None:
        """The README's format example names a Kit file too, and is no record."""
        self.assertFails(ADOPTED | {"docs/provenance/README.md": RECORD}, "src/Catalog/Money.cs:1: the comment says the code is adapted from something, and no record in docs/provenance/ names the file under Kit files")

    def test_an_adopted_test_without_a_provenance_record_fails(self) -> None:
        ported = {"tests/Shop.Tests/MoneyTests.cs": "public sealed class MoneyTests\n{\n    // Scenarios ported from the Till project's MoneyTests.\n}\n"}
        self.assertFails(ported, "tests/Shop.Tests/MoneyTests.cs:3: the comment says the code is ported from something, and no record in docs/provenance/ names the file under Kit files")

    def test_adopted_code_a_provenance_record_names_passes_and_so_does_a_string_that_says_the_words(self) -> None:
        quoted = {"src/Catalog/Messages.cs": 'public static class Messages { public const string Source = "taken from the header"; }\n'}
        self.assertPasses(ADOPTED | quoted | {"docs/provenance/Catalog.md": RECORD})

    def test_cited_kit_paths_that_exist_pass(self) -> None:
        """A path on a record's Kit files line, and in the plan a path with a `:line` suffix, a folder and a glob."""
        plan = "# Plan\n\n`src/PouryaKit.Shop/Cart.cs:3-7`, `src/PouryaKit.Shop/` and `src/PouryaKit.Shop/*.cs`.\n"
        self.assertPasses({"src/PouryaKit.Shop/Cart.cs": "public sealed class Cart;\n", "docs/foundation-review.md": plan, "docs/provenance/Catalog.md": KIT_RECORD})

    def test_a_cited_kit_file_that_does_not_exist_fails(self) -> None:
        """On a record's Kit files line and in its prose, and in the plan."""
        record = KIT_RECORD.replace("src/Catalog/Product.cs", "src/Catalog/Gone.cs") + "\nThe Kit checks it in `tests/Shop.Tests/GoneTests.cs`.\n"
        self.write({"docs/provenance/Catalog.md": record, "docs/foundation-review.md": "# Plan\n\nSee `src/PouryaKit.Shop/Gone.cs:12`.\n"})
        code, out = self.check()
        self.assertEqual(code, 1, out)
        self.assertIn("FAIL  docs/provenance/Catalog.md:5: `src/Catalog/Gone.cs` names no file or folder in the repository", out)
        self.assertIn("FAIL  docs/provenance/Catalog.md:10: `tests/Shop.Tests/GoneTests.cs` names no file or folder in the repository", out)
        self.assertIn("FAIL  docs/foundation-review.md:3: `src/PouryaKit.Shop/Gone.cs` names no file or folder in the repository", out)

    def test_an_upstream_path_in_a_provenance_record_or_the_plan_passes(self) -> None:
        """The record cites upstream paths into `src/` and `tests/` under Upstream paths and as `repository@commit path`;
        the plan cites one that way and one as a path of its own, outside the Kit's packages."""
        plan = "# Plan\n\nAs `till@abc1234 src/Till/Rules.cs:3` does, and `src/Till/Price.cs:8-12`.\n"
        self.assertPasses({"docs/provenance/Catalog.md": KIT_RECORD, "docs/foundation-review.md": plan})

    def test_a_generated_consumer_project_in_the_acceptance_record_passes(self) -> None:
        generated = "# Acceptance\n\nGenerated as `src/acme-shop.Host/acme-shop.Host.csproj` and `tests/acme-shop.Tests`, it passed.\n"
        self.assertPasses({"docs/acceptance.md": generated})

    def test_a_path_an_adr_cites_passes(self) -> None:
        self.assertPasses({"docs/adr/0008-carts.md": "# Carts\n\nThe cart lived in `src/PouryaKit.Shop/Cart.cs:3` until it moved.\n"})

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
