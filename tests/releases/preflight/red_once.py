#!/usr/bin/env python3
"""Run the guarantee tests with one production behaviour disabled per external copy."""

import importlib.util
import io
from pathlib import Path
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("proof_tests", HERE / "test_preflight.py")
tests = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tests)
original_entry, original_module = tests.ENTRY, tests.preflight
source = original_entry.read_text().replace("ROOT = Path(__file__).resolve().parents[3]", f"ROOT = Path({str(tests.ROOT)!r})")
source = source.replace('SDK = Path(__file__).parent / "sdk"', 'SDK = ROOT / "scripts/releases/preflight/sdk"')
controls = [
    ("compatible comparison", "test_compatible_patch_compares_real_artifacts", "for package_id in expected:", "for package_id in []:"),
    ("addition disposition", "test_addition_requires_exact_disposition_and_minor",
     "    dispositions(findings, review, candidate, baseline, candidate_version, baseline_version)", "    pass"),
    ("break migration notes", "test_break_requires_minor_notes_and_all_fresh_dispositions",
     'if expected == "reviewed-break" and not review.get("migration_notes", "").strip():', 'if False:'),
    ("first baseline record", "test_first_release_and_missing_or_wrong_baseline",
     '"fixture only: empty named history, no hosted baseline claim"', '""'),
    ("artifact checksum", "test_missing_and_changed_artifact_or_manifest", 'if digest(', 'if False and digest('),
    ("MinVer equality", "test_real_minver_refuses_metadata_and_competing_tag",
     'if any(properties[name] != requested for name in ("MinVerVersion", "PackageVersion", "Version")):', 'if False:'),
    ("SDK reference guard", "test_sdk_reference_failure_cannot_become_success", 'if available.returncode:', 'if False:'),
    ("managed asset guard", "test_all_managed_asset_groups_are_validated", 'if available.returncode:', 'if False:'),
    ("ASCII ordering", "test_semver_order_channels_and_published_collisions",
     '(left_field > right_field) - (left_field < right_field)', '(left_field.lower() > right_field.lower()) - (left_field.lower() < right_field.lower())'),
    ("reverse breaking classification", "test_parameter_name_change_is_break_in_both_directions",
     'if direction == "reverse" and detail.get("DiagnosticId") in ("CP0001", "CP0002") else', 'if direction == "reverse" else'),
    ("fixture evidence boundary", "test_compatible_patch_compares_real_artifacts",
     'result["publication_eligible"] = observed', 'result["publication_eligible"] = True'),
]
output = Path(tempfile.mkdtemp(prefix="pouryakit-preflight-red-"))
print(f"Evidence: {output}", flush=True)
tests.PreflightTests.setUpClass()
setup, teardown = tests.PreflightTests.setUpClass, tests.PreflightTests.tearDownClass
tests.PreflightTests.setUpClass = classmethod(lambda cls: None)
tests.PreflightTests.tearDownClass = classmethod(lambda cls: None)
try:
    for index, (behaviour, test, old, new) in enumerate(controls):
        if old not in source:
            raise RuntimeError(f"Stale red control: {behaviour}")
        entry = output / f"mutant-{index}.py"
        entry.write_text(source.replace(old, new))
        mutant_spec = importlib.util.spec_from_file_location(f"mutant_{index}", entry)
        mutant = importlib.util.module_from_spec(mutant_spec)
        mutant_spec.loader.exec_module(mutant)
        tests.ENTRY, tests.preflight = entry, mutant
        log = io.StringIO()
        result = unittest.TextTestRunner(stream=log, verbosity=2).run(unittest.TestSuite([tests.PreflightTests(test)]))
        (output / f"{index:02d}-{test}.log").write_text(log.getvalue())
        if result.wasSuccessful():
            raise AssertionError(f"Test remained green with {behaviour} disabled: {test}")
        print(f"RED {test}: {behaviour} disabled", flush=True)
finally:
    tests.ENTRY, tests.preflight = original_entry, original_module
    tests.PreflightTests.setUpClass, tests.PreflightTests.tearDownClass = setup, teardown
    teardown()
