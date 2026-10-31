#!/usr/bin/env python3
"""PreToolUse guard shared by Claude Code and Codex.

A seatbelt against accidents, not a sandbox. Denies file-tool writes outside the working
tree (reference projects are read-only), file-tool access to secret files, recursive
deletes, destructive pushes and skipped hooks. Hook input it cannot read or judge is allowed with a
warning, because a guard that denies every call locks the session; check-ai-setup.py
proves it still denies what it must.

POURYAKIT_GUARD=off in the environment allows every call. No settings file can switch off one
project hook, so this is how one person turns the guard off for their own Claude Code sessions:
an `env` entry in the gitignored .claude/settings.local.json.

Shell rules apply to `rm`, `find` and `git` at command position: the first word of a
statement (statements end at ; && || | ( ) ` and newlines), after VAR=value assignments,
shell keywords and wrappers such as sudo, env and xargs. After a wrapper every guarded word
in the statement is judged, because the guard cannot tell which wrapper options take a
value (`env -u git rm -rf build`). Redirections are skipped wherever they appear, so they
neither hide a command nor count as an argument. Quoted arguments and backslash-escaped
characters are data: a commit message or grep pattern that mentions a blocked command is
allowed, and the escaped parentheses of a find expression do not end its statement.
`$(...)` and backticks inside a double-quoted argument are checked too, and so is the
script given to `bash -c` (or sh, zsh, dash, ksh) and `eval`. A backslash-newline joins
what surrounds it, as the shell does. A command whose quoting cannot be parsed is matched
on every guarded word instead.

Every deny reason starts with MARKER; the kit-cockpit mod recognises this guard's denies by it.

One recursive delete is allowed, as a whitelist: an `rm` whose every argument is an option
or a literal absolute path under a system temp directory, with no `--`, and not run
through xargs (which appends operands the guard cannot see).

Not covered: deletes done by an interpreter or script (python, a .sh file, ssh); commands
built from variables or aliases; wrappers outside WRAPPERS; shell writes outside the
working tree; shell reads of secret files; commands that follow heredoc text whose
apostrophes pair up; a backtick substitution right after an escaped backslash in double quotes.
Over-blocked: a heredoc line that starts with a blocked command, even when the heredoc is
only data; `$(...)` in single quotes that sit inside double quotes, or with its `$` escaped;
a blocked command given as plain arguments after a wrapper (`sudo echo rm -rf x`,
`sudo git rm -r dir`).
"""

from __future__ import annotations

import json
import os
import re
import shlex
import sys
import tempfile
from pathlib import Path

MARKER = "PouryaKit guard: "
ROOT = Path(__file__).resolve().parents[2]
HOME = Path.home()
TEMP_ROOTS = {
    Path(p).resolve()
    for p in ("/tmp", "/var/tmp", "/var/folders", tempfile.gettempdir(), os.environ.get("TMPDIR"))
    if p
}
WRITABLE_ROOTS = {ROOT, (HOME / ".claude").resolve(), (HOME / ".codex").resolve()} | TEMP_ROOTS

ENV_TEMPLATES = {".env.example", ".env.sample", ".env.template", ".env.dist", ".env.defaults"}
READ_TOOLS = {"Read", "Grep"}
WRITE_TOOLS = {"Edit", "Write", "NotebookEdit"}
PATH_FIELDS = ("file_path", "notebook_path", "path", "glob")
PATCH_PREFIXES = ("*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: ")

PUNCTUATION = "();<>|&\n`"
OPERATOR = re.compile(r"(&>>?|[<>]{1,3}[&|]?)|.", re.S)  # group 1: a redirection; otherwise a separator
FD_NUMBER = re.compile(r"(?<![^\s;|&()`])\d+(?=[<>])")  # the 2 in 2>/dev/null
QUOTED = "\ue000"  # tags a quoted or escaped character, so ">", ";" or "(" as data is never read as an operator
SINGLE_QUOTED = {"$": "\ue001", "`": "\ue002"}  # the shell does not expand these in single quotes
RESTORE = {ord(mark): char for char, mark in SINGLE_QUOTED.items()}
SHELLS = {"bash", "sh", "zsh", "dash", "ksh"}
GUARDED = {"rm", "find", "git", "eval"} | SHELLS
WRAPPERS = {"sudo", "command", "exec", "time", "nohup", "env", "xargs"}
KEYWORDS = {"{", "!", "if", "then", "else", "elif", "do", "while", "until"}
GIT_VALUE_FLAGS = {"-C", "-c", "--git-dir", "--work-tree", "--namespace"}
ASSIGNMENT = re.compile(r"[A-Za-z_]\w*=")
SUBSTITUTION = re.compile(r"\$\(|(?<!\\)`")
SHORT_FLAGS = re.compile(r"-[A-Za-z]+")

RM_REASON = (
    "Recursive delete is blocked. Allowed only as a direct rm of literal absolute paths under a "
    "system temp directory, without -- and not through xargs. Otherwise remove exact files, or "
    "use the build tool's own clean command."
)


def under(path: Path, roots) -> bool:
    return any(path == root or root in path.parents for root in roots)


def is_secret(path: Path) -> bool:
    name = path.name.lower().strip("*?")  # a Grep glob such as .env* or *.env* names the same files
    if name == ".env" or name.startswith(".env."):
        return name not in ENV_TEMPLATES
    return name.endswith((".pem", ".key"))


def short_flag(token: str, letters: str) -> bool:
    return bool(SHORT_FLAGS.fullmatch(token)) and any(letter in token for letter in letters)


def temp_literal(token: str) -> bool:
    if not token.startswith("/") or ".." in token or re.search(r"[$`*?~{}\[\]]", token):
        return False
    return any(root in Path(token).resolve().parents for root in TEMP_ROOTS)


def mark_quotes(command: str) -> str | None:
    """Tag quoted words and neutralise `$` and backticks in single quotes; None if a quote never closes."""
    out, quote, i = [], "", 0
    while i < len(command):
        char, i = command[i], i + 1
        if char == "\\" and quote != "'":  # an escaped character is data: never a quote, operator or separator
            escaped, i = command[i : i + 1], i + 1
            if escaped == "\n":  # a line continuation: the shell drops the pair and joins what surrounds it
                continue
            char = QUOTED + char + escaped
        elif char == quote:
            quote = ""
        elif quote == "'":
            char = SINGLE_QUOTED.get(char, char)
        elif not quote and char in "\"'":
            quote, char = char, char + QUOTED
        out.append(char)
    return None if quote else "".join(out)


def tokenize(command: str) -> tuple[list[str], bool]:
    """Shell tokens, and whether the quoting was understood."""
    marked = mark_quotes(command)
    if marked is not None:
        lexer = shlex.shlex(marked, posix=True, punctuation_chars=PUNCTUATION)
        lexer.whitespace, lexer.whitespace_split, lexer.commenters = " \t\r", True, ""
        try:
            return list(lexer), True
        except ValueError:  # a trailing backslash
            pass
    joined = command.replace("\\\n", "")  # unbalanced quotes, as in some heredocs: every backslash-newline joins
    words = re.findall(r"[;|&()\n`<>]+|(?:\\.|[^\s;|&()`<>])+", joined)
    return [word.strip("\"'") for word in words], False


def statements(tokens: list[str]):
    """Split tokens into statements, dropping redirections and their operands."""
    current, skip = [], False
    for token in tokens:
        if token and set(token) <= set(PUNCTUATION):
            for operator in OPERATOR.finditer(token):
                skip = bool(operator.group(1))
                if not skip:
                    yield current
                    current = []
        elif skip:
            skip = False
        else:
            current.append(token.replace(QUOTED, ""))
    yield current


def commands(statement: list[str], parsed: bool):
    """Yield (name, arguments, via xargs) per command position; after a wrapper, every guarded word."""
    wrapped = xargs = False
    for index, token in enumerate(statement):
        name, rest = token.rsplit("/", 1)[-1], statement[index + 1 :]
        xargs = xargs or name == "xargs"
        if wrapped or not parsed:
            if name in GUARDED:
                yield name, rest, xargs
        elif ASSIGNMENT.match(token) or name in KEYWORDS:
            continue
        elif name in WRAPPERS:
            wrapped = True
        else:
            yield name, rest, xargs
            return


def git_subcommand(args: list[str]) -> tuple[str, list[str]]:
    skip = False
    for index, arg in enumerate(args):
        if skip:
            skip = False
        elif arg in GIT_VALUE_FLAGS:
            skip = True
        elif not arg.startswith("-"):
            return arg, args[index + 1 :]
    return "", []


def nested_script(name: str, args: list[str]) -> str | None:
    """The script that `eval` or a shell's -c option would run; the inner shell expands it."""
    if name == "eval":
        return " ".join(args).translate(RESTORE)
    if name in SHELLS:
        for index, arg in enumerate(args):
            if short_flag(arg, "c"):
                return " ".join(args[index + 1 : index + 2]).translate(RESTORE)
    return None


def command_reason(name: str, args: list[str], xargs: bool = False) -> str | None:
    options = args[: args.index("--")] if "--" in args else args  # after --, a -r is a file name
    if name == "rm" and any(arg == "--recursive" or short_flag(arg, "rR") for arg in options):
        paths = [arg for arg in args if arg == "-" or not arg.startswith("-")]
        if xargs or "--" in args or not paths or not all(temp_literal(path) for path in paths):
            return RM_REASON
    if name == "find":
        runs_rm = any(
            arg in ("-exec", "-execdir", "-ok") and args[i + 1 : i + 2] and args[i + 1].rsplit("/", 1)[-1] == "rm"
            for i, arg in enumerate(args)
        )
        if "-delete" in args or runs_rm:
            return "Recursive delete through find is blocked. Remove the exact files instead."
    if name == "git":
        subcommand, rest = git_subcommand(args)
        if subcommand == "clean" and any(short_flag(arg, "d") for arg in rest):
            return "git clean -d is blocked: it deletes untracked directories."
        if subcommand == "push" and any(
            arg in ("--force", "--mirror", "--delete") or short_flag(arg, "fd") or arg.startswith(("+", ":"))
            for arg in rest
        ):
            return "Force, mirror and delete pushes are blocked. --force-with-lease is allowed."
        if subcommand in ("commit", "merge") and (
            "--no-verify" in rest or (subcommand == "commit" and any(short_flag(arg, "n") for arg in rest))
        ):
            return "Skipping the hooks is blocked. Fix what they report, or stop and report it."
    return None


def shell_reason(command: str, depth: int = 0) -> str | None:
    tokens, parsed = tokenize(FD_NUMBER.sub("", command))
    for statement in statements(tokens):
        for name, args, xargs in commands(statement, parsed):
            script = nested_script(name, args) if depth < 3 else None
            reason = shell_reason(script, depth + 1) if script else command_reason(name, args, xargs)
            if reason:
                return reason
    if depth < 3:  # command substitution inside a double-quoted word, redirection operands included
        for token in tokens:
            for opener in SUBSTITUTION.finditer(token):
                reason = shell_reason(token[opener.end() :], depth + 1)
                if reason:
                    return reason
    return None


def file_reason(value: str, cwd: Path, writing: bool) -> str | None:
    asked = Path(value).expanduser()  # the name exactly as the tool was given it, quotes included
    asked = asked if asked.is_absolute() else cwd / asked
    target = asked.resolve()
    for path in (asked, target):  # the name asked for, and the file a symlink leads to
        if is_secret(path):
            return f"'{path.name}' is a secret file. Use a template such as .env.example."
    if writing and not under(target, WRITABLE_ROOTS):
        return (
            f"'{target}' is outside the PouryaKit working tree. Reference projects and other "
            "checkouts are read-only (AGENTS.md)."
        )
    return None


def denied_reason(tool: str, tool_input: dict, cwd: Path) -> str | None:
    command = tool_input.get("command")
    if isinstance(command, list):  # argv form: keep each argument one shell word
        join = " ".join if tool == "apply_patch" else shlex.join
        command = join(str(part) for part in command)
    command = command if isinstance(command, str) else ""
    if tool == "apply_patch":
        patch = command or str(tool_input.get("patch") or "")
        for line in patch.splitlines():
            for prefix in PATCH_PREFIXES:
                if line.startswith(prefix):
                    reason = file_reason(line.removeprefix(prefix).strip(), cwd, writing=True)
                    if reason:
                        return reason
        return None
    if tool.lower() in ("bash", "shell"):
        return shell_reason(command)
    if tool in READ_TOOLS or tool in WRITE_TOOLS:
        for field in PATH_FIELDS:
            value = tool_input.get(field)
            if isinstance(value, str) and value:
                reason = file_reason(value, cwd, writing=tool in WRITE_TOOLS)
                if reason:
                    return reason
    return None


def main() -> None:
    if os.environ.get("POURYAKIT_GUARD") == "off":
        print("{}")
        return
    reason = None
    try:
        data = json.load(sys.stdin)
        tool_input = data.get("tool_input")
        cwd = Path(str(data.get("cwd") or os.getcwd()))
        reason = denied_reason(str(data.get("tool_name") or ""), tool_input if isinstance(tool_input, dict) else {}, cwd)
    except Exception as error:  # unreadable input, or a value the rules cannot judge (an embedded NUL)
        print(f"guard.py: could not judge this hook input, allowing: {error}", file=sys.stderr)
    if reason:
        decision = {"hookEventName": "PreToolUse", "permissionDecision": "deny", "permissionDecisionReason": MARKER + reason}
        print(json.dumps({"hookSpecificOutput": decision}))
    else:
        print("{}")


if __name__ == "__main__":
    main()
