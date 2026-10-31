"""ci-local.sh reproduces CI while hosted runs are off, so a job or a step it leaves out runs nowhere at all."""

from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
LOCAL = (ROOT / ".agents/scripts/ci-local.sh").read_text(encoding="utf-8")
PACKAGE_JOB = (ROOT / ".github/workflows/package-validation.yml").read_text(encoding="utf-8")
JOBS = WORKFLOW[WORKFLOW.index("\njobs:\n") :]


class CiLocalTests(unittest.TestCase):
    def test_every_job_of_the_workflow_has_its_section(self) -> None:
        jobs = re.findall(r"^  ([a-z][a-z-]*):$", JOBS, re.M)
        self.assertIn("website", jobs)
        for job in jobs:
            self.assertIn(f'echo "== {job}"', LOCAL, f"ci-local.sh has no section for the {job} job")

    def test_every_run_step_of_the_workflow_is_reproduced(self) -> None:
        steps = re.findall(r"^\s+- run: (.+)$", JOBS, re.M)
        self.assertIn("pnpm --dir website lint", steps)
        for step in steps:
            self.assertIn(step, LOCAL, f"ci-local.sh does not run {step}")

    def test_the_commands_of_the_reusable_package_job_are_reproduced(self) -> None:
        # The two evidence.py steps read the Actions run, which a local run does not have.
        commands = [
            line.split('"$')[0].strip()
            for line in re.findall(r"^\s+(?:run: )?(python3 .+)$", PACKAGE_JOB, re.M)
            if "evidence.py" not in line
        ]
        self.assertTrue(any("validate.py" in command for command in commands))
        for command in commands:
            self.assertIn(command, LOCAL, f"ci-local.sh does not run {command}")


if __name__ == "__main__":
    unittest.main()
