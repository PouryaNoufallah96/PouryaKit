#!/usr/bin/env python3
"""Prints the failed tests of a failed GitHub Actions run, each with the lines around its assertion.

Usage: python3 .agents/scripts/ci-failures.py <run-id>

Reads the run's jobs with `gh run view <run-id> --json jobs`, fetches only the failed jobs' logs, strips colour codes
and timestamps, and prints each Microsoft.Testing.Platform `failed <Fully.Qualified.Test>` line with up to eight
indented lines after it: the assertion message and the first stack frames. A failed job with no failed test prints its
error lines instead (MSBuild `error <code>:` and `##[error]`). The log download dominates the runtime.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

CONTEXT = 8
MAX_ERRORS = 20
FAILED = {"failure", "timed_out"}
# gh prints an escape character as the two characters `^[`; a raw log carries the character itself.
COLOUR = re.compile(r"(?:\x1b|\^\[)\[[0-9;]*[A-Za-z]")
STAMP = re.compile(r"^.?\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z ")  # `.?` takes the first line's byte-order mark
TEST = re.compile(r"^failed (\S+\.\S+.*?)(?: \([\d hms]+\))?$")
ERROR = re.compile(r"^##\[error\]|: error [A-Z]+\d+: ")


def gh(*args: str) -> str:
    return subprocess.run(["gh", *args], capture_output=True, text=True, check=True).stdout


def clean(line: str) -> str:
    """`gh run view --log-failed` puts `<job>\\t<step>\\t` before each log line."""
    return STAMP.sub("", COLOUR.sub("", line.split("\t", 2)[-1])).rstrip()


def failed_tests(lines: list[str]) -> list[list[str]]:
    blocks: list[list[str]] = []
    for i, line in enumerate(lines):
        if not TEST.match(line):
            continue
        block = [line]
        for following in lines[i + 1 : i + 1 + CONTEXT]:
            if not following[:1].isspace():
                break
            block.append(following)
        if block not in blocks:
            blocks.append(block)
    return blocks


def main() -> int:
    if len(sys.argv) != 2 or not sys.argv[1].isdigit():
        print("usage: python3 .agents/scripts/ci-failures.py <run-id>", file=sys.stderr)
        return 2
    run_id = sys.argv[1]
    try:
        repo = gh("repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner").strip()
        jobs = [job for job in json.loads(gh("run", "view", run_id, "--repo", repo, "--json", "jobs"))["jobs"] if job.get("conclusion") in FAILED]
        with ThreadPoolExecutor(max(len(jobs), 1)) as pool:
            logs = list(pool.map(lambda job: gh("run", "view", "--repo", repo, "--job", str(job["databaseId"]), "--log-failed"), jobs))
    except subprocess.CalledProcessError as error:
        print(f"{' '.join(error.cmd)}: {error.stderr.strip()}", file=sys.stderr)
        return 1
    if not jobs:
        print(f"run {run_id} in {repo}: no failed job")
    for job, log in zip(jobs, logs):
        steps = ", ".join(step["name"] for step in job.get("steps", []) if step.get("conclusion") in FAILED) or "none reported"
        print(f"job '{job['name']}' ({job['databaseId']}) failed in: {steps}\n")
        lines = [clean(line) for line in log.splitlines()]
        blocks = failed_tests(lines)
        for block in blocks:
            print("\n".join(block), end="\n\n")
        if not blocks:
            errors = list(dict.fromkeys(line for line in lines if ERROR.search(line)))
            print("no failed test; its error lines:")
            print("\n".join(errors[:MAX_ERRORS]))
            if len(errors) > MAX_ERRORS:
                print(f"... and {len(errors) - MAX_ERRORS} more")
            print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
