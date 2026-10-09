#!/usr/bin/env python3
"""Save a subagent's final report from its task transcript.

Usage: save-report.py <task-output.jsonl> <dest.md> [--title "<title>"]

The harness writes each background agent's transcript as JSON lines; the report is the last text block of the last
assistant message. This writes it verbatim under a short header, so a role's report reaches the scratchpad with one
command (docs/agents/running-a-spec.md, "Reports go to the scratchpad"). Exits 1 when the transcript holds no
assistant text.
"""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path


def last_assistant_text(transcript: Path) -> tuple[str | None, str | None]:
    text, stamp = None, None
    for raw in transcript.read_text().splitlines():
        if not raw.strip():
            continue
        entry = json.loads(raw)
        if entry.get("type") != "assistant":
            continue
        blocks = (entry.get("message") or {}).get("content") or []
        texts = [b["text"] for b in blocks if isinstance(b, dict) and b.get("type") == "text" and b.get("text", "").strip()]
        if texts:
            text, stamp = texts[-1], entry.get("timestamp")
    return text, stamp


def main() -> int:
    args = sys.argv[1:]
    title = None
    if "--title" in args:
        i = args.index("--title")
        title = args[i + 1]
        del args[i : i + 2]
    if len(args) != 2:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    transcript, dest = Path(args[0]), Path(args[1])
    text, stamp = last_assistant_text(transcript)
    if text is None:
        print(f"no assistant text in {transcript}", file=sys.stderr)
        return 1
    when = stamp or datetime.now(timezone.utc).isoformat()
    header = f"# {title or dest.stem} (verbatim from the agent's final message, {when[:10]})\n\nTranscript: `{transcript}`\n\n"
    dest.write_text(header + text.rstrip() + "\n")
    print(f"saved {len(text)} chars to {dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
