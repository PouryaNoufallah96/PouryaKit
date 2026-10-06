"""Inventory failures at the evaluated-project seam, including accidentally packable samples."""
import unittest
from pathlib import Path
import tempfile
import os

from validate import check_inventory, check_cache_folders, check_consumer, isolated_env


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


if __name__ == "__main__":
    unittest.main()
