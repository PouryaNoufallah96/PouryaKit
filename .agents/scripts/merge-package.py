#!/usr/bin/env python3
"""Merges a package's reported head into the integration branch, checks the merged tree, and pushes only when it holds.

Usage:
  python3 .agents/scripts/merge-package.py <report.md> -m "<merge message>" --branch <integration branch> [--push]
  python3 .agents/scripts/merge-package.py --post-merge <head> --branch <integration branch> [--push]
  python3 .agents/scripts/merge-package.py --ready --branch <integration branch> [--base main]

<report.md> is the file save-report.py wrote; the last JSON object in it names `head` and `branch`. Before merging, the
script refuses `--branch main` (only the pull request merges into main), a current branch that is not `--branch`, a dirty
checkout, a head that is not a commit, a branch name that is not one (`pkg~1` names an older commit, not a branch), a
branch whose tip is not that head (the agent committed after reporting), a head already merged, and a head whose
code the package validation did not run on: the report names the commit `validate.py` ran on (`validated`, or `commit
<sha>` in `validate`), and every path that differs between that commit and the head must be Markdown, since a `.cs`,
props or script change after the validation is unproven. It runs `git merge --no-ff`. A conflict leaves the merge in progress: the orchestrator resolves and commits it, then runs the
--post-merge form. The post-merge checks run in order, each a process whose own exit code is read: the merged tree
against the head's, check-ai-setup.py, check-docs.py. A missing check script fails. --push pushes the current branch to
origin only when the tree equals the head's and every check passed. The last stdout line after a merge is
`merge=<sha7> tree=EQUAL|DIFFERS(<n>) checks=ok|FAIL(<names>) push=yes|no`; docs/agents/running-a-spec.md, "State and
handoff", says what a differing tree requires.

--ready is the close-out question, asked before the integration branch's pull request is marked ready: does the base
branch hold commits the branch lacks? It fetches origin/<base>. When the base holds such commits it lists them, the
paths a merge would conflict in (`git merge-tree --write-tree`) and what check-tree.py says of the merged tree, which
is checked out nowhere: the check runs in a scratch work tree that holds that tree's index and its .gitignore. It
merges, commits and pushes nothing, so the checkout may be dirty. Its last stdout line is
`ready=yes|no behind=<n> conflicts=<n> tree=ok|FAIL(<n>)`, and it exits 1 on `ready=no`.

Exit codes: 0 done; 1 a check failed, or --ready found the base ahead; 2 usage; 3 report unreadable; 4 dirty checkout;
5 head is not a commit; 6 no such branch; 7 branch tip is not the head; 8 not on the integration branch, or it is main;
9 head already merged (with --post-merge: not merged); 10 conflict, merge left in progress; 11 another git failure;
12 --push refused, the tree differs; 13 push failed; 14 the head holds code the validation did not run on.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
from pathlib import Path

TREE_CHECK = ".agents/scripts/check-tree.py"
CHECKS = (".agents/scripts/check-ai-setup.py", ".agents/scripts/check-docs.py")
(CHECK_FAILED, USAGE, REPORT, DIRTY, NOT_A_COMMIT, NO_BRANCH, BRANCH_MOVED, WRONG_BRANCH, MERGE_STATE, CONFLICT,
 GIT_FAILED, TREE_DIFFERS, PUSH_FAILED, UNVALIDATED) = range(1, 15)
HEAD = re.compile(r"[0-9a-f]{7,40}")
VALIDATED = re.compile(r"\b(?:commit|on)\s+([0-9a-f]{7,40})\b", re.IGNORECASE)  # "On commit f7abadc", "ran on ac0481c"



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


def read_report(path: Path) -> tuple[str, str, str]:
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
    if not isinstance(branch, str) or not branch:
        raise Stop(REPORT, f"the last JSON object in {path} has no branch name: {branch!r}")
    validated = found.get("validated")
    if not isinstance(validated, str) or not HEAD.fullmatch(validated):
        named = VALIDATED.search(found.get("validate", "") if isinstance(found.get("validate"), str) else "")
        validated = named.group(1) if named else None
    if validated is None:
        raise Stop(REPORT, f"the last JSON object in {path} names no commit the package validation ran on: add `validated` (7 to 40 hex digits), or `commit <sha>` in `validate`")
    return head, branch, validated


def require_validated(top: Path, head: str, validated: str) -> str:
    """The validation's commit is an ancestor of the head, and only Markdown differs between them: a `.cs`, props or
    script change after `validate.py` ran is unproven, and the implementer reruns it on the head."""
    full = commit(top, validated)
    if not full:
        raise Stop(NOT_A_COMMIT, f"the validated commit {validated} is not a commit in this repository")
    if git(top, "merge-base", "--is-ancestor", full, head).returncode:
        raise Stop(UNVALIDATED, f"the validated commit {validated[:7]} is not an ancestor of the head {head[:7]}: rerun validate.py on the head")
    code = [name for name in git_out(top, "diff", "--name-only", full, head).splitlines() if not name.endswith(".md")]
    if code:
        raise Stop(UNVALIDATED, f"the head {head[:7]} holds code the validation on {validated[:7]} did not run on; rerun validate.py on the head:\n" + "\n".join(f"  {name}" for name in code))
    print(f"validated={validated[:7]}: only Markdown differs between it and the head {head[:7]}")
    return full


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
    # A revision expression (pkg~1) resolves to an older commit, and --branch expands @{-1}, so the name must come back
    # unchanged and resolve as a ref, not as a revision.
    if git(top, "check-ref-format", "--branch", branch).stdout.strip() != branch:
        raise Stop(NO_BRANCH, f"{branch!r} is not a branch name")
    tip = git(top, "show-ref", "--verify", "--hash", f"refs/heads/{branch}").stdout.strip()
    if not tip:
        raise Stop(NO_BRANCH, f"branch {branch} does not exist")
    if tip != head:
        listing = git_out(top, "log", "--oneline", f"{head}..refs/heads/{branch}")
        why = ("the agent committed after reporting: merge its tip or ask it"
               if git(top, "merge-base", "--is-ancestor", head, tip).returncode == 0
               else "the branch no longer contains the reported head: ask the agent")
        raise Stop(BRANCH_MOVED, f"branch {branch} is at {tip[:7]}, not the reported head {head[:7]}:\n{listing}\n{why}")


def integration_branch(top: Path, wanted: str) -> str:
    if wanted == "main":
        raise Stop(WRONG_BRANCH, "main is never the integration branch: a package merges into the Wave's branch, and only the pull request merges into main")
    current = git(top, "symbolic-ref", "--quiet", "--short", "HEAD").stdout.strip()
    if not current:
        raise Stop(WRONG_BRANCH, "HEAD is detached; check out the integration branch")
    if wanted != current:
        raise Stop(WRONG_BRANCH, f"the current branch is {current}, not the integration branch {wanted}")
    print(f"integration branch: {current}")
    return current


def merge(top: Path, head: str, branch: str, message: str) -> None:
    if git(top, "merge-base", "--is-ancestor", head, "HEAD").returncode == 0:
        raise Stop(MERGE_STATE, f"{head[:7]} is already merged into HEAD; --post-merge {head[:7]} --branch {branch} runs the checks")
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
        lines.append(f"Resolve, commit, then run: python3 .agents/scripts/merge-package.py --post-merge {head[:7]} --branch {branch}")
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


def tree_offences(top: Path, tree: str) -> list[str]:
    """What check-tree.py says of a tree no checkout holds: one line per offence."""
    script = top / TREE_CHECK
    if not script.is_file():
        return [f"{TREE_CHECK} is missing; a missing check fails, it is never skipped"]
    with tempfile.TemporaryDirectory(prefix="merge-package-") as scratch:
        # The check asks git for the tracked paths and for what .gitignore ignores, so git is pointed at the tree.
        git_dir = git_out(top, "rev-parse", "--absolute-git-dir")
        env = os.environ | {"GIT_DIR": git_dir, "GIT_WORK_TREE": scratch, "GIT_INDEX_FILE": str(Path(scratch, "index"))}
        read = subprocess.run(["git", "read-tree", tree], cwd=scratch, env=env, capture_output=True, text=True)
        if read.returncode:
            raise Stop(GIT_FAILED, f"git read-tree {tree[:7]} failed: {read.stderr.strip()}")
        ignore = git(top, "show", f"{tree}:.gitignore")
        if ignore.returncode == 0:
            Path(scratch, ".gitignore").write_text(ignore.stdout, encoding="utf-8")
        paths = git_out(top, "ls-tree", "-r", "--name-only", tree)
        done = subprocess.run([sys.executable, str(script)], input=paths, cwd=scratch, env=env, capture_output=True, text=True)
    return done.stdout.splitlines() or ([f"check-tree exited {done.returncode}: {done.stderr.strip()}"] if done.returncode else [])


def ready(top: Path, base: str) -> int:
    remote = f"origin/{base}"
    git_out(top, "fetch", "--quiet", "origin", f"+refs/heads/{base}:refs/remotes/{remote}")
    behind = git_out(top, "log", "--oneline", f"HEAD..{remote}").splitlines()
    if not behind:
        print(f"{remote} has no commit that HEAD lacks\nready=yes behind=0 conflicts=0 tree=ok")
        return 0
    print(f"{remote} has {len(behind)} commit(s) that HEAD lacks:", *(f"  {line}" for line in behind), sep="\n")
    merged = git(top, "merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", remote)
    if merged.returncode not in (0, 1) or not merged.stdout.strip():  # 1 means the merge has conflicts
        raise Stop(GIT_FAILED, f"git merge-tree HEAD {remote} failed: {merged.stderr.strip()}")
    tree, *conflicts = merged.stdout.splitlines()
    if conflicts:
        print(f"a merge of {remote} would conflict in {len(conflicts)} path(s):", *(f"  {name}" for name in conflicts), sep="\n")
    offences = tree_offences(top, tree)
    if offences:
        print(f"check-tree refuses {len(offences)} thing(s) in the merged tree:", *(f"  {line}" for line in offences), sep="\n")
    print(f"Merge {remote} into the branch, resolve what is listed, run the ticket checks on the result, then run --ready again.")
    print(f"ready=no behind={len(behind)} conflicts={len(conflicts)} tree={'FAIL(' + str(len(offences)) + ')' if offences else 'ok'}")
    return CHECK_FAILED


def main() -> int:
    parser = argparse.ArgumentParser(usage=argparse.SUPPRESS, description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("report", nargs="?", type=Path, help="the report file save-report.py wrote")
    parser.add_argument("-m", dest="message", help="the merge commit's message")
    parser.add_argument("--post-merge", metavar="HEAD", help="check, and with --push push, a merge committed by hand after a conflict")
    parser.add_argument("--ready", action="store_true", help="only report whether origin/<base> holds commits the branch lacks, with the conflicts and tree offences a merge would bring")
    parser.add_argument("--base", default="main", help="with --ready: the branch the pull request merges into (default: main)")
    parser.add_argument("--push", action="store_true", help="push the current branch to origin when the tree is equal and every check passed")
    parser.add_argument("--branch", required=True, help="the integration branch, which must be the current branch; never main")
    parser.add_argument("--checks", nargs="+", metavar="COMMAND", help="tests only, refused unless MERGE_PACKAGE_TESTS=1 is set: run these commands instead of the two check scripts; each is split into an argument list and run without a shell")
    args = parser.parse_args()
    if args.checks and os.environ.get("MERGE_PACKAGE_TESTS") != "1":
        parser.error("--checks replaces the gating checks, so it runs only under the unit tests, with MERGE_PACKAGE_TESTS=1 set")
    if args.ready:
        if args.report or args.message or args.post_merge or args.push:
            parser.error("--ready takes --branch and --base only")
    elif args.post_merge:
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
        branch = integration_branch(top, args.branch)
        if args.ready:
            return ready(top, args.base)
        if args.post_merge:
            require_clean(top)
            head = require_head(top, args.post_merge)
            if git(top, "merge-base", "--is-ancestor", head, "HEAD").returncode:
                raise Stop(MERGE_STATE, f"{head[:7]} is not merged into HEAD; commit the merge first")
        else:
            reported, name, validated = read_report(args.report)
            require_clean(top)
            head = require_head(top, reported)
            require_tip(top, head, name)
            require_validated(top, head, validated)
            merge(top, head, branch, args.message)
        return post_merge(top, head, branch, checks, args.push)
    except Stop as stop:
        print(f"merge-package: {stop}", file=sys.stderr)
        return stop.code


if __name__ == "__main__":
    sys.exit(main())
