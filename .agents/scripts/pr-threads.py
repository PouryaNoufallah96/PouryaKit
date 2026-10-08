#!/usr/bin/env python3
"""Lists a pull request's unresolved review threads, answers and resolves them, and waits for its checks and reviews.

Usage:
  python3 .agents/scripts/pr-threads.py list <pr>
  python3 .agents/scripts/pr-threads.py reply-resolve <pr> <replies.json>
  python3 .agents/scripts/pr-threads.py wait <pr> [--timeout <seconds>] [--interval <seconds>]

Thin over `gh`, in the repository of the current directory.

list prints each unresolved thread as `### <thread id> comment <first comment's id> <path>:<line> by <author>`, then
the first comment's body without the reviewers' boilerplate: HTML comments, cubic's "Prompt for AI agents" block,
Codex's reaction line and its `<sub>` badge, whose priority is kept as `P2:`. Its last line counts them.

reply-resolve reads a JSON list of `{"thread": <thread id>, "comment": <first comment's id>, "body": <reply>}`, the two
ids as list prints them. For each it posts the reply, then resolves the thread, and prints one line with the outcome; a
thread whose reply failed is left unresolved. Exits 1 if any failed.

wait polls until no check is running and Codex's summary comment shows no review running, on two polls in a row,
because a check or review requested a moment ago is not listed yet. It then prints the checks that failed. A check
GitHub lists as in progress with a completion time has finished (pull request #65 kept one that way for good).
Exits 0 when none did, 1 when one did, 3 after the timeout (45 minutes unless --timeout says otherwise).
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path

FAILED, USAGE, TIMED_OUT = 1, 2, 3
THREADS = """query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) { pullRequest(number: $number) { reviewThreads(first: 100, after: $cursor) {
    pageInfo { hasNextPage endCursor }
    nodes { id isResolved isOutdated path line originalLine comments(first: 1) { nodes { databaseId author { login } body } } }
  } } }
}"""
RESOLVE = "mutation($thread: ID!) { resolveReviewThread(input: {threadId: $thread}) { thread { isResolved } } }"
BOILERPLATE = [
    (re.compile(r"<!--.*?-->", re.S), ""),
    (re.compile(r"<details>\s*<summary>Prompt for AI agents</summary>.*?</details>", re.S), ""),
    (re.compile(r"(?:<sub>)+!\[(P\d) Badge\]\([^)]*\)(?:</sub>)+\s*"), r"\1: "),
    (re.compile(r"<sub>(?:(?!<sub>).)*?</sub>", re.S), ""),
    (re.compile(r"^Useful\? React with .*$", re.M), ""),
    (re.compile(r"\n{3,}"), "\n\n"),
]
CODEX_SUMMARY, CODEX_RUNNING = "codex-pull-request-review-summary", "**Running**"


def gh(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["gh", *args], capture_output=True, text=True)


def gh_json(*args: str):
    done = gh(*args)
    if done.returncode:
        raise RuntimeError(f"gh {' '.join(args[:3])} failed: {done.stderr.strip()}")
    return json.loads(done.stdout)


def clean(body: str) -> str:
    for pattern, replacement in BOILERPLATE:
        body = pattern.sub(replacement, body)
    return body.strip()


def render(thread: dict) -> str:
    first = thread["comments"]["nodes"][0]
    author = (first.get("author") or {}).get("login", "ghost")
    outdated = " (outdated)" if thread.get("isOutdated") else ""
    line = thread.get("line") or thread.get("originalLine")
    return f"### {thread['id']} comment {first['databaseId']} {thread['path']}:{line}{outdated} by {author}\n\n{clean(first['body'])}\n"


def threads(repo: str, pr: int) -> list[dict]:
    owner, name = repo.split("/")
    found, cursor = [], None
    while True:
        page = gh_json("api", "graphql", "-f", f"query={THREADS}", "-f", f"owner={owner}", "-f", f"name={name}", "-F", f"number={pr}",
                       *(["-f", f"cursor={cursor}"] if cursor else []))["data"]["repository"]["pullRequest"]["reviewThreads"]
        found += page["nodes"]
        if not page["pageInfo"]["hasNextPage"]:
            return found
        cursor = page["pageInfo"]["endCursor"]


def list_threads(repo: str, pr: int) -> int:
    found = threads(repo, pr)
    unresolved = [thread for thread in found if not thread["isResolved"]]
    for thread in unresolved:
        print(render(thread))
    print(f"pr-threads: {len(unresolved)} unresolved of {len(found)} review thread(s) on #{pr}")
    return 0


def read_replies(path: Path) -> list[dict]:
    replies = json.loads(path.read_text(encoding="utf-8"))
    for reply in replies if isinstance(replies, list) else [None]:
        if not (isinstance(reply, dict) and isinstance(reply.get("thread"), str) and str(reply.get("comment", "")).isdigit()
                and isinstance(reply.get("body"), str) and reply["body"].strip()):
            raise ValueError(f'{path} must hold a list of {{"thread": "<id>", "comment": <id>, "body": "<reply>"}}; got {json.dumps(reply)[:120]}')
    return replies


def reply_resolve(repo: str, pr: int, replies: list[dict]) -> int:
    failed = 0
    for reply in replies:
        done = gh("api", "--method", "POST", f"repos/{repo}/pulls/{pr}/comments/{reply['comment']}/replies", "-f", f"body={reply['body']}")
        outcome = "replied"
        if done.returncode == 0:
            done = gh("api", "graphql", "-f", f"query={RESOLVE}", "-f", f"thread={reply['thread']}")
            outcome = "replied and resolved" if done.returncode == 0 else "replied, but NOT resolved"
        if done.returncode:
            failed += 1
            outcome = f"FAILED, {outcome if outcome != 'replied' else 'not replied and not resolved'}: {(done.stderr or done.stdout).strip()[:300]}"
        print(f"{reply['thread']} {outcome}")
    print(f"pr-threads: {len(replies) - failed} of {len(replies)} thread(s) answered and resolved on #{pr}")
    return FAILED if failed else 0


def unsettled(checks: list[dict] | None, comments: list[dict]) -> list[str]:
    """What is still running; `checks` is None while the head commit has no check yet."""
    running = {check["name"] for check in checks or [] if check["bucket"] == "pending" and check["completedAt"][:4] in ("", "0001")}
    waiting = ["no check reported yet"] if checks is None else [f"check {name}" for name in sorted(running)]
    if any(CODEX_SUMMARY in comment["body"] and CODEX_RUNNING in comment["body"] for comment in comments):
        waiting.append("the Codex review")
    return waiting


def wait(pr: int, timeout: float, interval: float) -> int:
    deadline, quiet = time.monotonic() + timeout, 0
    while True:
        listed = gh("pr", "checks", str(pr), "--json", "name,bucket,completedAt")  # exits non-zero while a check is pending or failed
        checks = json.loads(listed.stdout) if listed.stdout.strip() else None
        waiting = unsettled(checks, gh_json("pr", "view", str(pr), "--json", "comments")["comments"])
        quiet = 0 if waiting else quiet + 1
        if quiet == 2:
            failed = sorted({check["name"] for check in checks if check["bucket"] == "fail"})
            print(f"pr-threads: #{pr} is quiet: {len(checks)} check run(s), {len(failed)} failed{': ' + ', '.join(failed) if failed else ''}; no Codex review running")
            return FAILED if failed else 0
        print(f"waiting for {', '.join(waiting)}" if waiting else "quiet once; one more poll", flush=True)
        if time.monotonic() + interval > deadline:
            print(f"pr-threads: #{pr} was not quiet after {timeout:.0f} s")
            return TIMED_OUT
        time.sleep(interval)


def main() -> int:
    parser = argparse.ArgumentParser(usage=argparse.SUPPRESS, description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("action", choices=("list", "reply-resolve", "wait"))
    parser.add_argument("pr", type=int, help="the pull request's number")
    parser.add_argument("replies", nargs="?", type=Path, help="reply-resolve: the JSON file of replies")
    parser.add_argument("--timeout", type=float, default=2700, help="wait: seconds before it gives up (default 2700)")
    parser.add_argument("--interval", type=float, default=30, help="wait: seconds between polls (default 30)")
    args = parser.parse_args()
    if (args.action == "reply-resolve") != bool(args.replies):
        parser.error("reply-resolve takes the JSON file of replies, and only it does")
    try:
        if args.action == "wait":
            return wait(args.pr, args.timeout, args.interval)
        repo = gh_json("repo", "view", "--json", "nameWithOwner")["nameWithOwner"]
        if args.action == "list":
            return list_threads(repo, args.pr)
        return reply_resolve(repo, args.pr, read_replies(args.replies))
    except (RuntimeError, OSError, ValueError, KeyError) as error:
        print(f"pr-threads: {error}", file=sys.stderr)
        return FAILED


if __name__ == "__main__":
    sys.exit(main())
