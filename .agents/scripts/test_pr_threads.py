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


def thread(ident: str, body: str, *, resolved: bool = False, line: int | None = 13, author: str = "cubic-dev-ai", comment: int = 4219982016) -> dict:
    comment = {"databaseId": comment, "author": {"login": author}, "body": body}
    return {"id": ident, "isResolved": resolved, "isOutdated": line is None, "path": "src/Kit/AuthorizedOperation.cs", "line": line, "originalLine": 11, "comments": {"nodes": [comment]}}


def page(nodes: list[dict], cursor: str | None = None) -> dict:
    return {"data": {"repository": {"pullRequest": {"reviewThreads": {"pageInfo": {"hasNextPage": bool(cursor), "endCursor": cursor}, "nodes": nodes}}}}}


def check(name: str, status: str = "COMPLETED", conclusion: str = "SUCCESS") -> dict:
    return {"__typename": "CheckRun", "name": name, "status": status, "conclusion": conclusion}


def status(context: str, state: str) -> dict:
    return {"__typename": "StatusContext", "context": context, "state": state}


def view(checks: list[dict], codex: str = COMPLETED) -> tuple[int, dict]:
    """One poll's answer: the head commit's checks, and the comments with Codex's summary."""
    return 0, {"statusCheckRollup": checks, "comments": [{"body": "@codex review"}, {"body": SUMMARY.format(status=codex)}]}


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
        listed = page([thread("PRRT_a", CUBIC, comment=11), thread("PRRT_b", CUBIC, comment=22), thread("PRRT_c", CODEX, comment=33)])
        gh = Gh((0, listed), (0, {"id": 1}), (0, {}), (1, ""), (0, {"id": 3}), (1, ""))
        code, out = run(gh, pr_threads.reply_resolve, "o/r", 65, replies)
        self.assertEqual(code, 1, out)
        self.assertEqual(out.splitlines(), [
            "PRRT_a replied and resolved",
            "PRRT_b FAILED, not replied and not resolved: HTTP 422: refused",
            "PRRT_c FAILED, replied, but NOT resolved: HTTP 422: refused",
            "pr-threads: 1 of 3 thread(s) answered and resolved on #65",
        ])
        self.assertEqual(gh.calls[1], ("api", "--method", "POST", "repos/o/r/pulls/65/comments/11/replies", "-f", "body=Fixed in abc1234."))
        self.assertEqual(gh.calls[2][-1], "thread=PRRT_a")
        self.assertEqual(len(gh.calls), 6)  # the second thread's reply failed, so nothing asked to resolve it

    def test_reply_resolve_posts_nothing_when_an_entry_pairs_a_thread_with_a_comment_that_is_not_its_first(self) -> None:
        listed = page([thread("PRRT_a", CUBIC, comment=11), thread("PRRT_b", CUBIC, comment=22)])
        good = {"thread": "PRRT_a", "comment": 11, "body": "Fixed."}
        for bad, named in (({"thread": "PRRT_b", "comment": 11, "body": "Fixed."}, "PRRT_b with comment 11"),  # another thread's comment
                           ({"thread": "PRRT_of_another_pull_request", "comment": 22, "body": "Fixed."}, "PRRT_of_another_pull_request with comment 22"),
                           ({"thread": "PRRT_b", "comment": "23", "body": "Fixed."}, "PRRT_b with comment 23")):  # a reply in the thread
            gh = Gh((0, listed))
            with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, f"nothing was posted: no review thread of #65 has that id and that first comment: {named}$"):
                run(gh, pr_threads.reply_resolve, "o/r", 65, [good, bad])
            self.assertEqual(len(gh.calls), 1, gh.calls)  # the threads were read; neither a reply nor a resolution was sent

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

    def test_wait_returns_after_two_quiet_polls_once_nothing_runs(self) -> None:
        # GitHub kept pull request #65's publication-checks in progress for good; its conclusion says it passed.
        ended = [check("ai-setup"), check("publication-checks", "IN_PROGRESS", "SUCCESS"), check("docs", conclusion="SKIPPED"), status("CodeRabbit", "SUCCESS")]
        gh = Gh((0, {"statusCheckRollup": None, "comments": []}), view([check("ai-setup", "IN_PROGRESS", "")]), view([check("ai-setup"), status("CodeRabbit", "PENDING")]),
                view(ended, codex=RUNNING), view(ended), view(ended))
        code, out = run(gh, pr_threads.wait, 65, 600, 30)
        self.assertEqual(code, 0, out)
        self.assertEqual(out.splitlines(), [
            "waiting for no check reported yet",
            "waiting for check ai-setup",
            "waiting for check CodeRabbit",
            "waiting for the Codex review",
            "quiet once; one more poll",
            "pr-threads: #65 is quiet: 4 check(s): 4 passed, 0 failed, 0 cancelled, 0 with no outcome; no Codex review running",
        ])

    def test_wait_exits_1_for_a_check_that_ended_any_other_way_than_passing(self) -> None:
        for ended, counted in (
            (check("build", conclusion="FAILURE"), "1 passed, 1 failed (build), 0 cancelled, 0 with no outcome"),
            (check("build", "IN_PROGRESS", "FAILURE"), "1 passed, 1 failed (build), 0 cancelled, 0 with no outcome"),  # kept in progress, and failed
            (check("build", conclusion="TIMED_OUT"), "1 passed, 1 failed (build), 0 cancelled, 0 with no outcome"),
            (status("build", "ERROR"), "1 passed, 1 failed (build), 0 cancelled, 0 with no outcome"),
            (check("build", conclusion="CANCELLED"), "1 passed, 0 failed, 1 cancelled (build), 0 with no outcome"),
            (check("build", conclusion=""), "1 passed, 0 failed, 0 cancelled, 1 with no outcome (build)"),
            (check("build", conclusion="STALE"), "1 passed, 0 failed, 0 cancelled, 1 with no outcome (build)"),
            (status("build", ""), "1 passed, 0 failed, 0 cancelled, 1 with no outcome (build)"),
        ):
            with self.subTest(ended=ended):
                code, out = run(Gh(*[view([check("ai-setup"), ended])] * 2), pr_threads.wait, 65, 600, 30)
                self.assertEqual(code, 1, out)
                self.assertTrue(out.endswith(f"pr-threads: #65 is quiet: 2 check(s): {counted}; no Codex review running\n"), out)

    def test_wait_gives_up_after_the_timeout(self) -> None:
        code, out = run(Gh(*[view([check("ai-setup", "QUEUED", "")])] * 2), pr_threads.wait, 65, 45, 30)
        self.assertEqual(code, 3, out)
        self.assertTrue(out.endswith("pr-threads: #65 was not quiet after 45 s\n"), out)


if __name__ == "__main__":
    unittest.main()
