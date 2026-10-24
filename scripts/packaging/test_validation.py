"""Inventory failures at the evaluated-project seam, including accidentally packable samples."""
import unittest
from pathlib import Path
import tempfile
import os
import json
import subprocess
from unittest.mock import patch

import validate
from validate import check_inventory, check_cache_folders, check_consumer, check_production_publish, isolated_env, prepare_history


class HistoryTests(unittest.TestCase):
    def test_default_mode_fetches_remote_tags(self):
        with tempfile.TemporaryDirectory() as folder:
            origin = Path(folder) / "origin"
            root = Path(folder) / "repo"
            output = Path(folder) / "evidence"
            output.mkdir()
            subprocess.run(["git", "init", "-q", str(origin)], check=True)
            subprocess.run(["git", "-C", str(origin), "-c", "user.name=Proof", "-c", "user.email=proof@example.invalid",
                            "commit", "-q", "--allow-empty", "-m", "fixture"], check=True)
            subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "clone", "-q", "--bare", str(origin), str(root)], check=True)
            subprocess.run(["git", "-C", str(origin), "tag", "v0.1.0-rc.2"], check=True)
            with patch.object(validate, "ROOT", root):
                prepare_history(output)
            document = json.loads((output / "history.json").read_text())
            self.assertEqual("fetch", document["mode"])
            self.assertEqual(["v0.1.0-rc.2"], document["tags"])
            self.assertTrue((output / "tags.log").is_file())

    def test_checkout_mode_uses_existing_history_and_tags_without_origin_credentials(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "repo"
            root.mkdir()
            output = Path(folder) / "evidence"
            output.mkdir()
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            subprocess.run(["git", "-C", str(root), "-c", "user.name=Proof", "-c", "user.email=proof@example.invalid",
                            "commit", "-q", "--allow-empty", "-m", "fixture"], check=True)
            subprocess.run(["git", "-C", str(root), "tag", "v0.1.0-rc.1"], check=True)
            with patch.object(validate, "ROOT", root):
                commit = prepare_history(output, fetch=False)
            document = json.loads((output / "history.json").read_text())
            self.assertEqual({"mode": "checkout", "shallow": False, "commit": commit, "tags": ["v0.1.0-rc.1"]}, document)

    def test_checkout_mode_rejects_shallow_history(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / "repo"
            subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "clone", "-q", "--bare", "--depth=1",
                            validate.ROOT.as_uri(), str(root)], check=True)
            with patch.object(validate, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "requires full history"):
                    prepare_history(Path(folder), fetch=False)
            self.assertFalse((Path(folder) / "history.json").exists())


class SinceTests(unittest.TestCase):
    EXEMPT = ["docs/acceptance.md", "docs/agents/ai-setup.md", ".agents/scripts/check-docs.py", ".claude/agent-memory/kit-implementer/MEMORY.md",
              ".codex/agents/kit-implementer.toml", ".githooks/pre-commit", "AGENTS.md", "CLAUDE.md", "GLOSSARY.md", "README.md", "skills-lock.json"]

    def test_only_documentation_and_the_ai_setup_need_no_validation(self):
        self.assertEqual([], validate.needing_validation(self.EXEMPT))
        for path in ("src/PouryaKit.Domain/Result.cs", "samples/ModularMonolith/src/ModularMonolith.Host/Program.cs",
                     "tests/PouryaKit.ArchitectureTests/PackageArtifacts.cs", "tests/ModularMonolith.Tests/CachingTests.cs",
                     "scripts/packaging/validate.py", "scripts/packaging/inventory.json", "THIRD-PARTY-NOTICES.md", "LICENSE",
                     "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "nuget.config",
                     "PouryaKit.slnx", ".config/dotnet-tools.json", ".editorconfig", ".github/workflows/package-validation.yml",
                     "docs.md", "src/PouryaKit.Templates/content/shared/README.md", "a-new-directory/file.txt"):
            with self.subTest(path=path):
                self.assertEqual([path], validate.needing_validation([*self.EXEMPT, path]))

    def test_the_validation_runs_unless_every_committed_uncommitted_and_untracked_change_is_exempt(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*arguments):
                subprocess.run(["git", "-C", folder, "-c", "user.name=Proof", "-c", "user.email=proof@example.invalid",
                                "-c", "core.hooksPath=/dev/null", *arguments], check=True, capture_output=True)

            def write(path, text):
                (root / path).parent.mkdir(parents=True, exist_ok=True)
                (root / path).write_text(text)

            def exit_code(*arguments):
                with patch.object(validate, "ROOT", root), patch.object(validate, "validate") as full, \
                        patch("sys.argv", ["validate.py", "--no-fetch", "--output", str(root / "evidence"), *arguments]):
                    return validate.main(), full.called

            def changed(ref):
                with patch.object(validate, "ROOT", root):
                    return validate.changed_since(ref)

            git("init", "-q")
            write("docs/guide.md", "# Guide\n")
            write("src/Kit/A.cs", "class A;\n")
            write("old/name.txt", "moved\n")
            git("add", "-A")
            git("commit", "-q", "-m", "base")
            self.assertEqual((0, False), exit_code("--since", "HEAD"))
            write("docs/guide.md", "# Guide\n\nMore.\n")
            git("commit", "-q", "-am", "docs")
            write("AGENTS.md", "# Agents\n")
            self.assertEqual(["AGENTS.md", "docs/guide.md"], changed("HEAD~1"))
            self.assertEqual((0, False), exit_code("--since", "HEAD~1"))
            self.assertEqual((0, True), exit_code())
            write("scripts/new.py", "pass\n")
            self.assertEqual((0, True), exit_code("--since", "HEAD~1"))
            (root / "scripts/new.py").unlink()
            write("src/Kit/A.cs", "class A { }\n")
            self.assertEqual((0, True), exit_code("--since", "HEAD~1"))
            git("checkout", "-q", "--", "src/Kit/A.cs")
            git("mv", "old/name.txt", "docs/name.txt")
            self.assertEqual(["AGENTS.md", "docs/guide.md", "docs/name.txt", "old/name.txt"], changed("HEAD~1"))
            self.assertEqual((1, False), exit_code("--since", "no-such-ref"))
            self.assertFalse((root / "evidence").exists())


class InventoryTests(unittest.TestCase):
    def test_inventory_is_nonempty_and_exact(self):
        expected = [{"id": "PouryaKit.Domain"}]
        evaluated = {"src/PouryaKit.Domain/PouryaKit.Domain.csproj": {
            "PackageId": "PouryaKit.Domain", "IsPackable": "true", "EnablePackageValidation": "true"}}
        check_inventory(expected, evaluated)
        for inventory in ([], [{"id": "PouryaKit.Extra"}], expected * 2):
            with self.subTest(inventory=inventory), self.assertRaises(ValueError):
                check_inventory(inventory, evaluated)
        for key, value in (("PackageId", "PouryaKit.Wrong"), ("IsPackable", "false"), ("EnablePackageValidation", "false")):
            changed = {path: {**values, key: value} for path, values in evaluated.items()}
            with self.subTest(key=key), self.assertRaises(ValueError):
                check_inventory(expected, changed)
        with self.assertRaises(ValueError):
            check_inventory(expected, {**evaluated, "samples/Sample.csproj": {
                "IsPackable": "true", "PackageId": "Sample", "EnablePackageValidation": "false"}})


class ConsumerCacheTests(unittest.TestCase):
    def test_isolated_caches_are_outside_the_consumer_source_glob(self):
        with tempfile.TemporaryDirectory() as folder:
            consumer = Path(folder) / "consumers/Domain"
            env = isolated_env(consumer)
            for variable in ("NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "DOTNET_CLI_HOME"):
                self.assertFalse(Path(env[variable]).is_relative_to(consumer))
                self.assertTrue(Path(env[variable]).is_relative_to(Path(folder)))

    def test_a_single_cache_accepts_sdk_keys_with_or_without_a_separator(self):
        with tempfile.TemporaryDirectory() as folder:
            cache = Path(folder) / "cache/packages"
            for key in (str(cache), str(cache) + os.sep):
                check_cache_folders([key], str(cache))
            for keys in ([], [str(cache), str(cache)], [str(cache.parent / "substitute")]):
                with self.subTest(keys=keys), self.assertRaises(RuntimeError):
                    check_cache_folders(keys, str(cache))

    def test_actual_consumer_assets_without_a_trailing_separator_pass(self):
        # SDK 10's packageFolders object has a separator-free key. Exercise the public consumer check with that shape.
        import json
        with tempfile.TemporaryDirectory() as folder:
            consumer = Path(folder) / "consumers/Domain"
            assets = consumer / "obj/project.assets.json"
            assets.parent.mkdir(parents=True)
            cache = consumer / "cache/packages"
            assets.write_text(json.dumps({"packageFolders": {str(cache): {}}, "libraries": {}}))
            self.assertEqual([str(assets)], check_consumer(consumer, {"NUGET_PACKAGES": str(cache)}, [], "unused", []))


class TestClosureTests(unittest.TestCase):
    """The generated monolith's tests run through PouryaKit.Testing, which depends on Modularity; its Host never does."""
    KIT = ["PouryaKit.Modularity", "PouryaKit.Testing", "PouryaKit.Web"]
    REFUSED = ["PouryaKit.Modularity", "PouryaKit.Testing"]

    @staticmethod
    def consumer(folder, projects):
        """A template consumer whose projects, `{folder: [package IDs]}`, restored those Kit packages from the produced feed."""
        output = Path(folder)
        consumer = output / "consumers/template-monolith"
        produced = output / "packages"
        produced.mkdir(parents=True)
        env = isolated_env(consumer)
        cache = Path(env["NUGET_PACKAGES"])
        for project, ids in projects.items():
            libraries = {}
            for package in ids:
                (produced / f"{package}.1.0.0.nupkg").write_bytes(package.encode())
                restored = cache / package.lower() / "1.0.0"
                restored.mkdir(parents=True, exist_ok=True)
                (restored / f"{package.lower()}.1.0.0.nupkg").write_bytes(package.encode())
                (restored / ".nupkg.metadata").write_text(json.dumps({"source": str(produced)}))
                libraries[f"{package}/1.0.0"] = {"type": "package", "path": f"{package.lower()}/1.0.0"}
            assets = consumer / project / "obj/project.assets.json"
            assets.parent.mkdir(parents=True)
            assets.write_text(json.dumps({"packageFolders": {str(cache): {}}, "libraries": libraries}))
        return consumer, env

    def test_the_test_project_restores_testing_and_modularity_which_the_runtime_closure_refuses(self):
        with tempfile.TemporaryDirectory() as folder:
            consumer, env = self.consumer(folder, {
                "Acme.Shop/src/Acme.Shop.Host": ["PouryaKit.Web"],
                "Acme.Shop/tests/Acme.Shop.Tests": ["PouryaKit.Web", "PouryaKit.Testing", "PouryaKit.Modularity"]})
            self.assertEqual(2, len(check_consumer(consumer, env, self.KIT, "1.0.0", self.REFUSED)))

    def test_the_hosts_closure_and_its_release_publish_refuse_modularity(self):
        with tempfile.TemporaryDirectory() as folder:
            consumer, env = self.consumer(folder, {
                "Acme.Shop/src/Acme.Shop.Host": ["PouryaKit.Web", "PouryaKit.Modularity"],
                "Acme.Shop/tests/Acme.Shop.Tests": ["PouryaKit.Web", "PouryaKit.Testing", "PouryaKit.Modularity"]})
            with self.assertRaisesRegex(RuntimeError, r"excluded dependencies outside its tests: \['PouryaKit.Modularity', 'PouryaKit.Web'\]"):
                check_consumer(consumer, env, self.KIT, "1.0.0", self.REFUSED)
            host = Path(folder) / "Acme.Shop.Host"
            published = Path(folder) / "publish"
            (host / validate.GENERATED_REGISTRY).parent.mkdir(parents=True)
            (host / validate.GENERATED_REGISTRY).write_text("// generated")
            published.mkdir()
            (published / "Acme.Shop.Host.dll").write_bytes(b"MZ GeneratedHandlerRegistry")
            (published / "PouryaKit.Web.dll").write_bytes(b"MZ")
            self.assertEqual([validate.GENERATED_REGISTRY], check_production_publish(host, published, self.REFUSED))
            (published / "PouryaKit.Modularity.dll").write_bytes(b"MZ")
            with self.assertRaisesRegex(RuntimeError, "carries PouryaKit.Modularity.dll, which its runtime closure refuses"):
                check_production_publish(host, published, self.REFUSED)


class ProductionPublishTests(unittest.TestCase):
    def test_a_publish_with_the_generated_code_and_no_runtime_compiler_passes_and_any_other_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            host = Path(folder) / "Acme.Shop.Host"
            published = Path(folder) / "publish"
            (host / validate.GENERATED_REGISTRY).parent.mkdir(parents=True)
            (host / validate.GENERATED_REGISTRY).write_text("// generated")
            published.mkdir()
            (published / "Acme.Shop.Host.dll").write_bytes(b"MZ GeneratedHandlerRegistry")
            self.assertEqual([validate.GENERATED_REGISTRY], check_production_publish(host, published))

            for compiler in validate.RUNTIME_COMPILER:
                (published / compiler).write_bytes(b"MZ")
                with self.subTest(compiler=compiler), self.assertRaisesRegex(RuntimeError, "carries the runtime compiler"):
                    check_production_publish(host, published)
                (published / compiler).unlink()
            (published / "Acme.Shop.Host.dll").write_bytes(b"MZ")
            with self.assertRaisesRegex(RuntimeError, "built with the generated code"):
                check_production_publish(host, published)
            (host / validate.GENERATED_REGISTRY).unlink()
            with self.assertRaisesRegex(RuntimeError, "codegen write wrote no"):
                check_production_publish(host, published)


class FinishTests(unittest.TestCase):
    """A PASS leaves no 8 GB evidence directory behind unless asked (Wave 3 session 3: 61 of them filled the disk)."""

    def test_a_pass_removes_the_evidence_directory(self):
        with tempfile.TemporaryDirectory() as parent:
            output = Path(parent) / "evidence"
            output.mkdir()
            (output / "validation.log").write_text("PASS")
            validate.finish(output, keep=False)
            self.assertFalse(output.exists())

    def test_keep_retains_the_evidence_directory(self):
        with tempfile.TemporaryDirectory() as parent:
            output = Path(parent) / "evidence"
            output.mkdir()
            validate.finish(output, keep=True)
            self.assertTrue(output.exists())

    def test_main_finishes_only_after_a_pass(self):
        with tempfile.TemporaryDirectory() as parent, patch.object(validate, "validate", side_effect=RuntimeError("boom")), \
                patch("sys.argv", ["validate.py", "--no-fetch", "--output", parent + "/kept"]):
            Path(parent, "kept").mkdir()
            self.assertEqual(validate.main(), 1)
            self.assertTrue(Path(parent, "kept").exists())
        with tempfile.TemporaryDirectory() as parent, patch.object(validate, "validate", return_value=None), \
                patch("sys.argv", ["validate.py", "--no-fetch", "--output", parent + "/gone"]):
            Path(parent, "gone").mkdir()
            self.assertEqual(validate.main(), 0)
            self.assertFalse(Path(parent, "gone").exists())

    def test_an_output_that_holds_files_is_refused_before_any_work_unless_kept(self):
        with tempfile.TemporaryDirectory() as parent, patch.object(validate, "validate", return_value=None) as full:
            output = Path(parent, "evidence")
            output.mkdir()
            (output / "revision.json").write_text("{}")
            with patch("sys.argv", ["validate.py", "--no-fetch", "--output", str(output)]):
                self.assertEqual(validate.main(), 1)
            full.assert_not_called()
            self.assertTrue((output / "revision.json").exists())
            # CI's flow: its revision record is written into the directory first, and it passes --keep.
            with patch("sys.argv", ["validate.py", "--no-fetch", "--keep", "--output", str(output)]):
                self.assertEqual(validate.main(), 0)
            full.assert_called_once()
            self.assertTrue((output / "revision.json").exists())


if __name__ == "__main__":
    unittest.main()
