"""Inventory failures at the evaluated-project seam, including accidentally packable samples."""
import unittest

from validate import check_inventory


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


if __name__ == "__main__":
    unittest.main()
