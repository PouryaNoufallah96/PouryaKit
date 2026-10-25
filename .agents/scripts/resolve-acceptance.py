#!/usr/bin/env python3
"""Resolve the by-design merge conflict in docs/acceptance.md: two tickets inserted their ordered section at the same
place. Keeps every `## <prefix> ..., ticket #N` section from both sides of each conflict block, in ascending ticket order,
for the prefixes check-docs.py orders (`ORDERED_SECTIONS`).

Usage: resolve-acceptance.py [path]   (default docs/acceptance.md; exits 1 when the file holds no conflict block)

Written four times as a per-session tool in Waves 3 and 4 (docs/agents/running-a-spec.md, "Merging with diagram
conflicts"); here so a merger finds it where the other scripts are.
"""
import re
import sys
from pathlib import Path

import importlib.util

_spec = importlib.util.spec_from_file_location("check_docs", Path(__file__).resolve().with_name("check-docs.py"))
_check_docs = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_check_docs)
ORDERED_SECTIONS = _check_docs.ORDERED_SECTIONS  # the one source of the ordered prefixes

HEADING = re.compile(r"^## (?:" + "|".join(map(re.escape, ORDERED_SECTIONS)) + r") .*, ticket #(?P<ticket>\d+)\s*$", re.M)
SPLIT = re.compile(r"(?m)^(?=## (?:" + "|".join(map(re.escape, ORDERED_SECTIONS)) + r") )")
CONFLICT = re.compile(r"<<<<<<< [^\n]*\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n", re.S)


def ticket(section: str) -> int:
    match = HEADING.search(section)
    if match is None:
        sys.exit(f"a conflicted block holds text that is not an ordered section:\n{section[:200]}")
    return int(match.group("ticket"))


def resolve(text: str) -> tuple[str, list[int]]:
    tickets: list[int] = []

    def merge(match: re.Match[str]) -> str:
        sections = [part for part in SPLIT.split(match.group(1) + match.group(2)) if part.strip()]
        sections.sort(key=ticket)
        tickets.extend(ticket(part) for part in sections)
        return "\n".join(part.rstrip("\n") + "\n" for part in sections)

    resolved, count = CONFLICT.subn(merge, text)
    if count == 0:
        sys.exit("no conflict block")
    return resolved, tickets


def main() -> int:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/acceptance.md")
    resolved, tickets = resolve(path.read_text(encoding="utf-8"))
    if "<<<<<<<" in resolved or ">>>>>>>" in resolved:
        sys.exit("a conflict marker remains")
    path.write_text(resolved, encoding="utf-8")
    print(f"sections: {tickets}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
