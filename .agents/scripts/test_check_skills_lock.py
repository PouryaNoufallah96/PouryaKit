"""check-skills-lock.mjs on a small tree: a skill hashed in the skills CLI's file order passes, and a changed or missing one fails."""

from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("check-skills-lock.mjs")
# The skills CLI's order, localeCompare: lowercase "agents/…" before "SKILL.md". A byte sort puts "SKILL.md" first.
FILES = (("agents/openai.yaml", b"policy:\n  allow_implicit_invocation: false\n"), ("SKILL.md", b"---\nname: demo\n---\n"))


def expected() -> str:
    digest = hashlib.sha256()
    for path, content in FILES:
        digest.update(path.encode())
        digest.update(content)
    return digest.hexdigest()


@unittest.skipUnless(shutil.which("node"), "node is not on PATH")
class CheckSkillsLock(unittest.TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.root)
        for path, content in FILES:
            file = self.root / ".agents/skills/demo" / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(content)
        self.pin(expected())

    def pin(self, computed: str, name: str = "demo") -> None:
        entry = {"source": "example/skills", "sourceType": "github", "skillPath": f"skills/{name}/SKILL.md", "computedHash": computed}
        (self.root / "skills-lock.json").write_text(json.dumps({"version": 1, "skills": {name: entry}}, indent=2) + "\n")

    def run_script(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(["node", str(SCRIPT), *args, str(self.root)], capture_output=True, text=True)

    def test_a_skill_hashed_in_the_cli_order_passes(self) -> None:
        done = self.run_script()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)

    def test_a_symlink_is_not_hashed(self) -> None:
        (self.root / ".agents/skills/demo/link.md").symlink_to("SKILL.md")
        done = self.run_script()
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)

    def test_a_changed_file_fails_and_names_the_skill(self) -> None:
        (self.root / ".agents/skills/demo/SKILL.md").write_bytes(b"---\nname: demo\n---\nan edit\n")
        done = self.run_script()
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("FAIL  skills-lock.json: demo:", done.stdout)

    def test_an_entry_without_its_directory_fails(self) -> None:
        self.pin(expected(), name="gone")
        done = self.run_script()
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("FAIL  skills-lock.json: gone: .agents/skills/gone is missing", done.stdout)

    def test_write_pins_the_directory_as_it_is(self) -> None:
        self.pin("0" * 64)
        self.assertEqual(self.run_script("--write").returncode, 0)
        lock = json.loads((self.root / "skills-lock.json").read_text())
        self.assertEqual(lock["skills"]["demo"]["computedHash"], expected())


if __name__ == "__main__":
    unittest.main()
