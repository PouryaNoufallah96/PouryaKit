#!/usr/bin/env python3
"""Pin a diagram source to a revision and, unless --pin-only, refinalize its page with Archify.

Usage: repin-diagram.py <worktree> <full revision sha> <slug>.<type> [--pin-only] [--archify <dir>]

<type> is the source's kind (`architecture`, `workflow`, `sequence`, `lifecycle`): the source is
docs/architecture/diagrams/<slug>.<type>.json and the page <slug>.html. The Archify checkout is `--archify`, else
$ARCHIFY_HOME, the directory that holds bin/archify.mjs (the v3.0.1 clone keeps it one level down:
<clone>/archify). Chrome is $ARCHIFY_CHROME, else Playwright's headless shell under ~/Library/Caches/ms-playwright.
Prints the finalize result's ok, status, gates and diagnostics, removes the receipt files Archify leaves beside the page,
and prints both files' SHA-256. Exit code is finalize's.

A merge that re-ranges a diagram pins it to the merge commit and refinalizes (docs/agents/running-a-spec.md, "Merging
with diagram conflicts").
"""
import argparse
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

RECEIPTS = (".browser-check.json", ".delivery.json", ".finalize.json", ".finalize-summary.json", ".delivery-pending.json")


def chrome_path() -> str:
    if (env := os.environ.get("ARCHIFY_CHROME")):
        return env
    candidates = sorted(glob.glob(str(Path.home() / "Library/Caches/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-mac-arm64/chrome-headless-shell")))
    if not candidates:
        sys.exit("no Chrome: set ARCHIFY_CHROME or install Playwright's chromium (npx playwright install chromium)")
    return candidates[-1]


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("worktree", type=Path)
    parser.add_argument("revision")
    parser.add_argument("name", metavar="slug.type")
    parser.add_argument("--pin-only", action="store_true")
    parser.add_argument("--archify", type=Path, default=Path(os.environ.get("ARCHIFY_HOME", "")))
    arguments = parser.parse_args(argv)
    root, revision, name, pin_only, archify = arguments.worktree, arguments.revision, arguments.name, arguments.pin_only, arguments.archify
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        sys.exit("revision must be a full 40-character SHA")
    slug, kind = name.rsplit(".", 1)
    diagrams = root / "docs/architecture/diagrams"
    source, page = diagrams / f"{slug}.{kind}.json", diagrams / f"{slug}.html"
    text = source.read_text(encoding="utf-8")
    current = json.loads(text)["meta"]["repository"]["revision"]
    if text.count(f'"revision": "{current}"') != 1:
        sys.exit("revision line not found once")
    source.write_text(text.replace(f'"revision": "{current}"', f'"revision": "{revision}"'), encoding="utf-8")
    print(f"{source.name}: revision {current[:8]} -> {revision[:8]}")
    if pin_only:
        return 0
    if not (archify / "bin/archify.mjs").is_file():
        sys.exit(f"no Archify at {archify or '<unset>'}: pass --archify <dir> or set ARCHIFY_HOME to the directory holding bin/archify.mjs")
    for suffix in RECEIPTS:
        (diagrams / f"{slug}{suffix}").unlink(missing_ok=True)
    run = subprocess.run(
        ["node", "bin/archify.mjs", "finalize", kind, str(source), str(page), "--repo-root", str(root), "--quality", "showcase", "--json"],
        cwd=archify, env={**os.environ, "ARCHIFY_CHROME": chrome_path()}, capture_output=True, text=True,
    )
    print(f"finalize exit {run.returncode}")
    try:
        result = json.loads(run.stdout)
        print(json.dumps({k: result.get(k) for k in ("ok", "status", "gates", "diagnostics")}, default=str)[:1500])
    except json.JSONDecodeError:
        print(run.stdout[-2000:])
        print(run.stderr[-2000:])
    for suffix in RECEIPTS:
        (diagrams / f"{slug}{suffix}").unlink(missing_ok=True)
    for path in (source, page):
        print(path.name, hashlib.sha256(path.read_bytes()).hexdigest())
    return run.returncode


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
