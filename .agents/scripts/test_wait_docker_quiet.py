"""The Docker waiter's lock recovery: its functions sourced in bash with the lock in a temporary directory, so neither the
machine's lock nor another agent's waiter is touched. The waits themselves (30 s polls) are proven by a recorded run
(docs/acceptance.md, ticket #176)."""

from __future__ import annotations

import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().with_name("wait-docker-quiet.sh")


def dead_pid() -> int:
    """A PID that ran and is gone."""
    process = subprocess.Popen(["true"])
    process.wait()
    return process.pid


class WaitDockerQuietTests(unittest.TestCase):
    def setUp(self) -> None:
        temp = tempfile.TemporaryDirectory(prefix="wait-docker-quiet-")
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.lock = self.root / "lock"

    def call(self, function: str, argument: str = "") -> int:
        """Exit code of one of the script's functions, sourced with no arguments and the lock moved to the test's."""
        line = f'script=$1; path=$2; argument=$3; set --; source "$script"; lock=$path; {function} $argument'
        done = subprocess.run(["bash", "-c", line, "harness", str(SCRIPT), str(self.lock), argument], capture_output=True, text=True)
        self.assertEqual("", done.stderr)
        return done.returncode

    def hold(self, pid: int | None) -> None:
        self.lock.mkdir()
        if pid is not None:
            (self.lock / "pid").write_text(f"{pid}\n")

    def leftovers(self) -> list[str]:
        return sorted(path.name for path in self.root.iterdir() if path.name != "lock")

    def test_a_lock_whose_holder_is_gone_is_reclaimed_and_a_running_holders_kept(self):
        self.hold(dead_pid())
        self.assertEqual(1, self.call("locked"))
        self.assertFalse(self.lock.exists())
        # PID 1 belongs to another user, where `kill -0` fails with EPERM: still a running holder.
        for running in (os.getpid(), 1):
            with self.subTest(holder=running):
                self.hold(running)
                self.assertEqual(0, self.call("locked"))
                self.assertEqual(f"{running}\n", (self.lock / "pid").read_text())
                (self.lock / "pid").unlink()
                self.lock.rmdir()
        self.assertEqual([], self.leftovers())

    def test_a_lock_without_a_pid_is_being_taken_for_a_minute_then_reclaimed(self):
        self.hold(None)
        self.assertEqual(0, self.call("locked"))
        self.assertTrue(self.lock.is_dir())
        aged = time.time() - 120
        os.utime(self.lock, (aged, aged))
        self.assertEqual(1, self.call("locked"))
        self.assertFalse(self.lock.exists())

    def test_a_reclaimer_that_judged_an_older_holder_dead_leaves_the_lock_taken_since(self):
        # Two waiters read one dead holder; the first reclaimed and took the lock before the second renames it.
        self.hold(os.getpid())
        self.assertEqual(1, self.call("reclaim", str(dead_pid())))
        self.assertEqual(f"{os.getpid()}\n", (self.lock / "pid").read_text())
        self.assertEqual([], self.leftovers())
        # The second's rename finds no lock at all when the first has reclaimed it and not yet taken it.
        (self.lock / "pid").unlink()
        self.lock.rmdir()
        self.assertEqual(1, self.call("reclaim", str(dead_pid())))
        self.assertEqual([], [path.name for path in self.root.iterdir()])


if __name__ == "__main__":
    unittest.main()
