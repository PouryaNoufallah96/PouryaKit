#!/usr/bin/env python3
"""Merges a package's reported head into the integration branch, checks the merged tree, and pushes only when it holds.

Usage:
  python3 .agents/scripts/merge-package.py <report.md> -m "<merge message>" [--push] [--branch <integration branch>]
  python3 .agents/scripts/merge-package.py --post-merge <head> [--push]

<report.md> is the file save-report.py wrote; the last JSON object in it names `head` and `branch`. Before merging, the
script refuses a dirty checkout, a head that is not a commit, a branch whose tip is not that head (the agent committed
after reporting), a current branch that is not the integration branch, and a head already merged. It runs
`git merge --no-ff`. A conflict leaves the merge in progress: the orchestrator resolves and commits it, then runs the
--post-merge form. The post-merge checks run in order, each a process whose own exit code is read: the merged tree
against the head's, check-ai-setup.py, check-docs.py. A missing check script fails. --push pushes the current branch to
origin only when the tree equals the head's and every check passed. The last stdout line after a merge is
`merge=<sha7> tree=EQUAL|DIFFERS(<n>) checks=ok|FAIL(<names>) push=yes|no`; docs/agents/running-a-spec.md, "State and
handoff", says what a differing tree requires.

Exit codes: 0 done; 1 a check failed; 2 usage; 3 report unreadable; 4 dirty checkout; 5 head is not a commit; 6 no such
branch; 7 branch tip is not the head; 8 not on the integration branch; 9 head already merged (with --post-merge: not
merged); 10 conflict, merge left in progress; 11 another git failure; 12 --push refused, the tree differs; 13 push failed.
"""

from __future__ import annotations

import argparse
import json
import re
import shlex
import subprocess
import sys
from pathlib import Path

CHECKS = (".agents/scripts/check-ai-setup.py", ".agents/scripts/check-docs.py")
(CHECK_FAILED, USAGE, REPORT, DIRTY, NOT_A_COMMIT, NO_BRANCH, BRANCH_MOVED, WRONG_BRANCH, MERGE_STATE, CONFLICT,
 GIT_FAILED, TREE_DIFFERS, PUSH_FAILED) = range(1, 14)
HEAD = re.compile(r"[0-9a-f]{7,40}")
BRANCH = re.compile(r"[^\s-]\S*")  # no leading dash, so git never reads it as an option


class Stop(Exception):
    """A refusal or a stopped merge: the message is printed and the code is the exit code."""

    def __init__(self, code: int, message: str) -> None:
        super().__init__(message)
        self.code = code


def git(top: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["git", *args], cwd=top, capture_output=True, text=True)


def git_out(top: Path, *args: str) -> str:
    done = git(top, *args)
    if done.returncode:
        raise Stop(GIT_FAILED, f"git {' '.join(args)} failed: {done.stderr.strip()}")
    return done.stdout.strip()


def commit(top: Path, ref: str) -> str:
    """The full hash of `ref`, or "" when it names no commit."""
    done = git(top, "rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}")
    return done.stdout.strip() if done.returncode == 0 else ""


def read_report(path: Path) -> tuple[str, str]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as error:
        raise Stop(REPORT, f"cannot read the report {path}: {error.strerror}")
    # Top-level objects only: after one parses, the scan resumes past its end, so a nested object is never "the last".
    decoder, found, at = json.JSONDecoder(), None, text.find("{")
    while at != -1:
        try:
            found, end = decoder.raw_decode(text, at)
        except ValueError:
            end = at + 1
        at = text.find("{", end)
    if found is None:
        raise Stop(REPORT, f"the report {path} holds no JSON object")
    head, branch = found.get("head"), found.get("branch")
    if not isinstance(head, str) or not HEAD.fullmatch(head):
        raise Stop(REPORT, f"the last JSON object in {path} has no head commit (7 to 40 hex digits): {head!r}")
    if not isinstance(branch, str) or not BRANCH.fullmatch(branch):
        raise Stop(REPORT, f"the last JSON object in {path} has no branch name: {branch!r}")
    return head, branch


def require_clean(top: Path) -> None:
    status = git(top, "status", "--porcelain")
    if status.returncode or status.stdout.strip():
        raise Stop(DIRTY, f"the checkout is not clean; commit or remove these first:\n{(status.stdout or status.stderr).rstrip()}")


def require_head(top: Path, head: str) -> str:
    full = commit(top, head)
    if not full:
        raise Stop(NOT_A_COMMIT, f"head {head} is not a commit in this repository")
    return full


def require_tip(top: Path, head: str, branch: str) -> None:
    tip = commit(top, f"refs/heads/{branch}")
    if not tip:
        raise Stop(NO_BRANCH, f"branch {branch} does not exist")
    if tip != head:
        listing = git_out(top, "log", "--oneline", f"{head}..refs/heads/{branch}")
        why = ("the agent committed after reporting: merge its tip or ask it"
               if git(top, "merge-base", "--is-ancestor", head, tip).returncode == 0
               else "the branch no longer contains the reported head: ask the agent")
        raise Stop(BRANCH_MOVED, f"branch {branch} is at {tip[:7]}, not the reported head {head[:7]}:\n{listing}\n{why}")


def integration_branch(top: Path, wanted: str | None) -> str:
    current = git(top, "symbolic-ref", "--quiet", "--short", "HEAD").stdout.strip()
    if not current:
        raise Stop(WRONG_BRANCH, "HEAD is detached; check out the integration branch")
    if wanted and wanted != current:
        raise Stop(WRONG_BRANCH, f"the current branch is {current}, not the integration branch {wanted}")
    print(f"integration branch: {current}" + ("" if wanted else " (the current branch, as no --branch was given)"))
    return current


def merge(top: Path, head: str, message: str) -> None:
    if git(top, "merge-base", "--is-ancestor", head, "HEAD").returncode == 0:
        raise Stop(MERGE_STATE, f"{head[:7]} is already merged into HEAD; --post-merge {head[:7]} runs the checks")
    done = git(top, "merge", "--no-ff", "-m", message, head)
    if done.returncode == 0:
        print(done.stdout.strip())
        return
    conflicted = git(top, "diff", "--name-only", "--diff-filter=U").stdout.splitlines()
    in_progress = bool(commit(top, "MERGE_HEAD"))
    if conflicted and in_progress:
        lines = [f"the merge of {head[:7]} stopped on conflicts and is left in progress:", *(f"  {name}" for name in conflicted)]
        if "docs/acceptance.md" in conflicted:
            lines.append("docs/acceptance.md: both sides appended a section; keep both sections, in ticket order.")
        lines.append(f"Resolve, commit, then run: python3 .agents/scripts/merge-package.py --post-merge {head[:7]}")
        raise Stop(CONFLICT, "\n".join(lines))
    stopped = "\nthe merge is still in progress; `git merge --abort` drops it" if in_progress else ""
    raise Stop(GIT_FAILED, f"git merge failed: {(done.stderr or done.stdout).strip()}{stopped}")


def check_name(argv: list[str]) -> str:
    return Path(next((arg for arg in argv if arg.endswith(".py")), argv[0])).stem


def run_check(top: Path, argv: list[str]) -> bool:
    name = check_name(argv)
    script = next((top / arg for arg in argv if arg.endswith(".py")), None)
    if script is not None and not script.is_file():
        shown = script.relative_to(top) if script.is_relative_to(top) else script
        print(f"check {name}: FAIL, {shown} is missing; a missing check fails, it is never skipped")
        return False
    try:
        done = subprocess.run(argv, cwd=top, capture_output=True, text=True)
    except OSError as error:
        print(f"check {name}: FAIL, it did not start: {error}")
        return False
    if output := (done.stdout + done.stderr).strip():
        print(output)
    print(f"check {name}: " + ("ok" if done.returncode == 0 else f"FAIL (exit {done.returncode})"))
    return done.returncode == 0


def post_merge(top: Path, head: str, branch: str, checks: list[list[str]], push: bool) -> int:
    merged = git_out(top, "rev-parse", "--short=7", "HEAD")
    same = git(top, "diff", "--quiet", head, "HEAD")
    if same.returncode not in (0, 1):
        raise Stop(GIT_FAILED, f"git diff --quiet {head[:7]} HEAD failed: {same.stderr.strip()}")
    if same.returncode == 0:
        tree = "EQUAL"
        print(f"tree=EQUAL: the tree of {merged} is the tree of {head[:7]}")
    else:
        files = git_out(top, "diff", "--name-only", head, "HEAD").splitlines()
        tree = f"DIFFERS({len(files)})"
        print(f"tree=DIFFERS: {len(files)} file(s) of {merged} differ from {head[:7]}:", *(f"  {name}" for name in files), sep="\n")
        print("The ticket's verify must run on the merged tree; the report's verify does not prove it.")
    failed = [check_name(argv) for argv in checks if not run_check(top, argv)]
    code, pushed = (CHECK_FAILED if failed else 0), "no"
    if push and failed:
        print("not pushed: a check failed")
    elif push and tree != "EQUAL":
        print("not pushed: the merged tree differs from the reported head")
        code = TREE_DIFFERS
    elif push:
        done = git(top, "push", "origin", branch)
        print((done.stdout + done.stderr).strip())
        code, pushed = (PUSH_FAILED, "no") if done.returncode else (0, "yes")
    print(f"merge={merged} tree={tree} checks={'FAIL(' + ','.join(failed) + ')' if failed else 'ok'} push={pushed}")
    return code


def main() -> int:
    parser = argparse.ArgumentParser(usage=argparse.SUPPRESS, description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", nargs="?", type=Path, help="the report file save-report.py wrote")
    parser.add_argument("-m", dest="message", help="the merge commit's message")
    parser.add_argument("--post-merge", metavar="HEAD", help="check, and with --push push, a merge committed by hand after a conflict")
    parser.add_argument("--push", action="store_true", help="push the current branch to origin when the tree is equal and every check passed")
    parser.add_argument("--branch", help="the integration branch; the default is the current branch")
    parser.add_argument("--checks", nargs="+", metavar="COMMAND", help="tests only: run these commands instead of the two check scripts; each is split into an argument list and run without a shell")
    args = parser.parse_args()
    if args.post_merge:
        if args.report or args.message:
            parser.error("--post-merge takes no report and no -m")
        if not HEAD.fullmatch(args.post_merge):
            parser.error("--post-merge takes the reported head: 7 to 40 hex digits")
    elif not args.report or not args.message:
        parser.error("pass the report file and -m <merge message>, or --post-merge <head>")
    found = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    if found.returncode:
        print("merge-package: run it inside the repository's checkout", file=sys.stderr)
        return USAGE
    top = Path(found.stdout.strip())
    checks = [shlex.split(command) for command in args.checks] if args.checks else [[sys.executable, str(top / script)] for script in CHECKS]
    try:
        if args.post_merge:
            require_clean(top)
            head = require_head(top, args.post_merge)
            branch = integration_branch(top, args.branch)
            if git(top, "merge-base", "--is-ancestor", head, "HEAD").returncode:
                raise Stop(MERGE_STATE, f"{head[:7]} is not merged into HEAD; commit the merge first")
        else:
            reported, name = read_report(args.report)
            require_clean(top)
            head = require_head(top, reported)
            require_tip(top, head, name)
            branch = integration_branch(top, args.branch)
            merge(top, head, args.message)
        return post_merge(top, head, branch, checks, args.push)
    except Stop as stop:
        print(f"merge-package: {stop}", file=sys.stderr)
        return stop.code


if __name__ == "__main__":
    sys.exit(main())
