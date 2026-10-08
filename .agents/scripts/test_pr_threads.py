"""pr-threads.py on canned `gh` output, in the shapes pull request #65 returned: no network, and `gh` never runs."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("pr_threads", Path(__file__).with_name("pr-threads.py"))
pr_threads = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pr_threads)

CUBIC = """<!-- cubic:v=8dadcbd3 -->
<!-- metadata:{"confidence":7} -->
P1: `_ended` is read on another thread; make the flag `volatile`.

<details>
<summary>Prompt for AI agents</summary>

```text
Check if this issue is valid. At src/Kit/AuthorizedOperation.cs, line 13:
<comment>`_ended` is read on another thread.</comment>
```

</details>

```suggestion
    private volatile bool _ended;
```"""
CODEX = """**<sub><sub>![P2 Badge](https://img.shields.io/badge/P2-yellow?style=flat)</sub></sub>  Include the assembly in cache keys**

Two types of one full name share an entry.

Useful? React with 👍 / 👎."""
SUMMARY = "<!-- codex-pull-request-review-summary -->\n\n| Review | Status |\n| --- | --- |\n| 📝 **Code Review** | {status} |\n"
RUNNING, COMPLETED = "🔄 **Running** since <relative-time>", "✅ **Completed** <relative-time>"


def thread(ident: str, body: str, *, resolved: bool = False, line: int | None = 13, author: str = "cubic-dev-ai") -> dict:
    comment = {"databaseId": 4219982016, "author": {"login": author}, "body": body}
    return {"id": ident, "isResolved": resolved, "isOutdated": line is None, "path": "src/Kit/AuthorizedOperation.cs", "line": line, "originalLine": 11, "comments": {"nodes": [comment]}}


def page(nodes: list[dict], cursor: str | None = None) -> dict:
    return {"data": {"repository": {"pullRequest": {"reviewThreads": {"pageInfo": {"hasNextPage": bool(cursor), "endCursor": cursor}, "nodes": nodes}}}}}


def check(name: str, bucket: str, completed: str = "0001-01-01T00:00:00Z") -> dict:
    return {"name": name, "bucket": bucket, "completedAt": completed}


class Gh:
    """Stands in for `gh`: answers each call with the next canned (exit code, stdout) and records the arguments."""

    def __init__(self, *answers: tuple[int, object]) -> None:
        self.answers, self.calls = list(answers), []

    def __call__(self, *args: str) -> subprocess.CompletedProcess[str]:
        self.calls.append(args)
        code, out = self.answers.pop(0)
        return subprocess.CompletedProcess(args, code, out if isinstance(out, str) else json.dumps(out), "HTTP 422: refused" if code else "")


def run(gh: Gh, action, *args) -> tuple[int, str]:
    out, clock = io.StringIO(), [0.0]  # a clock that only a sleep moves
    with patch.object(pr_threads, "gh", gh), patch.object(pr_threads.time, "monotonic", lambda: clock[0]), \
            patch.object(pr_threads.time, "sleep", lambda seconds: clock.__setitem__(0, clock[0] + seconds)), contextlib.redirect_stdout(out):
        return action(*args), out.getvalue()


class PrThreadsTests(unittest.TestCase):
    def test_the_reviewers_boilerplate_is_stripped_and_the_finding_kept(self) -> None:
        self.assertEqual(pr_threads.clean(CUBIC), "P1: `_ended` is read on another thread; make the flag `volatile`.\n\n```suggestion\n    private volatile bool _ended;\n```")
        self.assertEqual(pr_threads.clean(CODEX), "**P2: Include the assembly in cache keys**\n\nTwo types of one full name share an entry.")

    def test_list_prints_the_unresolved_threads_of_every_page_with_their_ids(self) -> None:
        gh = Gh((0, page([thread("PRRT_a", CUBIC), thread("PRRT_b", CUBIC, resolved=True)], cursor="next")),
                (0, page([thread("PRRT_c", CODEX, line=None, author="chatgpt-codex-connector")])))
        code, out = run(gh, pr_threads.list_threads, "o/r", 65)
        self.assertEqual(code, 0, out)
        self.assertIn("### PRRT_a comment 4219982016 src/Kit/AuthorizedOperation.cs:13 by cubic-dev-ai\n\nP1: `_ended`", out)
        self.assertIn("### PRRT_c comment 4219982016 src/Kit/AuthorizedOperation.cs:11 (outdated) by chatgpt-codex-connector\n\n**P2: Include", out)
        self.assertNotIn("PRRT_b", out)
        self.assertTrue(out.endswith("pr-threads: 2 unresolved of 3 review thread(s) on #65\n"), out)
        self.assertNotIn("cursor=next", gh.calls[0])
        self.assertIn("cursor=next", gh.calls[1])

    def test_reply_resolve_leaves_a_thread_unresolved_when_its_reply_fails_and_goes_on(self) -> None:
        replies = [{"thread": "PRRT_a", "comment": 11, "body": "Fixed in abc1234."}, {"thread": "PRRT_b", "comment": 22, "body": "Not changed."},
                   {"thread": "PRRT_c", "comment": 33, "body": "Fixed."}]
        gh = Gh((0, {"id": 1}), (0, {}), (1, ""), (0, {"id": 3}), (1, ""))
        code, out = run(gh, pr_threads.reply_resolve, "o/r", 65, replies)
        self.assertEqual(code, 1, out)
        self.assertEqual(out.splitlines(), [
            "PRRT_a replied and resolved",
            "PRRT_b FAILED, not replied and not resolved: HTTP 422: refused",
            "PRRT_c FAILED, replied, but NOT resolved: HTTP 422: refused",
            "pr-threads: 1 of 3 thread(s) answered and resolved on #65",
        ])
        self.assertEqual(gh.calls[0], ("api", "--method", "POST", "repos/o/r/pulls/65/comments/11/replies", "-f", "body=Fixed in abc1234."))
        self.assertEqual(gh.calls[1][-1], "thread=PRRT_a")
        self.assertEqual(len(gh.calls), 5)  # the second thread's reply failed, so nothing asked to resolve it

    def test_a_replies_file_with_a_missing_id_or_an_empty_body_is_refused_whole(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "replies.json"
            for bad in ({"thread": "PRRT_a", "body": "Fixed."}, {"thread": "PRRT_a", "comment": 11, "body": " "}, {"comment": 11, "body": "Fixed."}, "PRRT_a"):
                path.write_text(json.dumps([{"thread": "PRRT_a", "comment": 11, "body": "Fixed."}, bad]))
                with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, "must hold a list of"):
                    pr_threads.read_replies(path)
            path.write_text(json.dumps({"thread": "PRRT_a", "comment": 11, "body": "Fixed."}))
            with self.assertRaisesRegex(ValueError, "must hold a list of"):
                pr_threads.read_replies(path)

    def test_wait_returns_after_two_quiet_polls_and_names_a_failed_check(self) -> None:
        running = {"comments": [{"body": "@codex review"}, {"body": SUMMARY.format(status=RUNNING)}]}
        done = {"comments": [{"body": SUMMARY.format(status=COMPLETED)}]}
        finished = [check("ai-setup", "pass", "2026-10-08T21:08:55Z"), check("build-and-test / validate", "fail", "2026-10-08T21:18:57Z"),
                    check("publication-checks", "pending", "2026-10-08T21:08:50Z")]  # in progress for good, with a completion time
        gh = Gh((1, ""), (0, done),  # the head commit has no check yet
                (8, [check("ai-setup", "pending")]), (0, done),
                (0, finished), (0, running),
                (1, finished), (0, done),
                (1, finished), (0, done))
        code, out = run(gh, pr_threads.wait, 65, 600, 30)
        self.assertEqual(code, 1, out)
        self.assertEqual(out.splitlines(), [
            "waiting for no check reported yet",
            "waiting for check ai-setup",
            "waiting for the Codex review",
            "quiet once; one more poll",
            "pr-threads: #65 is quiet: 3 check run(s), 1 failed: build-and-test / validate; no Codex review running",
        ])

    def test_wait_gives_up_after_the_timeout(self) -> None:
        gh = Gh(*[(8, [check("ai-setup", "pending")]), (0, {"comments": []})] * 2)
        code, out = run(gh, pr_threads.wait, 65, 45, 30)
        self.assertEqual(code, 3, out)
        self.assertTrue(out.endswith("pr-threads: #65 was not quiet after 45 s\n"), out)


if __name__ == "__main__":
    unittest.main()
