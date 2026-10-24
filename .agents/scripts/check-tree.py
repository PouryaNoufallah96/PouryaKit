#!/usr/bin/env python3
"""Fails on a path outside the repository's top-level entries, and on a .gitignore line that ignores a tracked path.

Takes repository-relative paths as arguments, or one per line on stdin, and judges the repository of the current
directory. Exits 1 with one line per offence. `.githooks/pre-commit` runs it on the staged paths and
.agents/scripts/check-ai-setup.py on `git ls-files`, so CI catches what a commit without the hook let through.
"""

from __future__ import annotations

import subprocess
import sys

# The top-level entries the tree holds; a new one is added here in the same change that adds it.
TOP_LEVEL = {
    ".agents", ".claude", ".codex", ".config", ".githooks", ".github", ".vscode",
    "docs", "samples", "scripts", "spikes", "src", "tests",
    ".editorconfig", ".gitattributes", ".gitignore", "AGENTS.md", "CLAUDE.md", "Directory.Build.props",
    "Directory.Build.targets", "Directory.Packages.props", "GLOSSARY.md", "LICENSE", "PouryaKit.slnx", "README.md",
    "THIRD-PARTY-NOTICES.md", "global.json", "nuget.config", "skills-lock.json",
}


def main() -> int:
    if not sys.argv[1:] and sys.stdin.isatty():
        # No paths and nothing piped: a bare call would wait on the terminal forever (Wave 3 session 6 lost a chained command).
        print("usage: check-tree.py <path>... | <paths on stdin>, for example: git ls-files | python3 .agents/scripts/check-tree.py", file=sys.stderr)
        return 2
    paths = [p for p in (sys.argv[1:] or sys.stdin.read().splitlines()) if p]
    offences = [f"{p}: '{p.split('/')[0]}' is not a top-level entry of this repository" for p in paths if p.split("/")[0] not in TOP_LEVEL]
    if ".gitignore" in paths:
        # Every tracked path the root .gitignore matches, .gitignore itself included. A `!` line re-includes, so it never counts.
        top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
        tracked = subprocess.run(["git", "ls-files"], capture_output=True, text=True, check=True, cwd=top).stdout
        matched = subprocess.run(["git", "check-ignore", "--no-index", "--verbose", "--stdin"], input=tracked, capture_output=True, text=True, cwd=top)
        if matched.returncode not in (0, 1):  # 1 means nothing matched
            offences.append(f".gitignore: git check-ignore failed: {matched.stderr.strip()}")
        for line in matched.stdout.splitlines():
            where, _, path = line.partition("\t")
            source, number, pattern = where.split(":", 2)
            if source == ".gitignore" and not pattern.startswith("!"):
                offences.append(f".gitignore:{number}: '{pattern}' ignores the tracked path {path}")
    for offence in offences:
        print(offence)
    return 1 if offences else 0


if __name__ == "__main__":
    sys.exit(main())
