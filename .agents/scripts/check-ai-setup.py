#!/usr/bin/env python3
"""Checks that the AI setup is consistent across Claude Code and Codex; exits 1 on any failure."""

from __future__ import annotations

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GUARD = ROOT / ".agents/hooks/guard.py"
MOD_HOOKS = ROOT / ".claude/mods/kit-cockpit/hooks/register.ts"
STATUSLINE = ROOT / ".claude/statusline.sh"
AGENT_KEYS = {
    "name", "description", "tools", "disallowedTools", "model", "permissionMode", "maxTurns",
    "skills", "mcpServers", "hooks", "memory", "background", "omitClaudeMd", "effort",
    "isolation", "color", "initialPrompt", "experimental",
}  # the frontmatter table in code.claude.com/docs/en/sub-agents; unknown keys are ignored silently
OUTPUT_STYLE_KEYS = {"name", "description", "keep-coding-instructions"}  # code.claude.com/docs/en/output-styles
AGENTS_MD_LIMIT = 32768  # Codex project_doc_max_bytes default
GUARDED_TOOLS = {  # the tools the guard judges; each hooks file must send them to it
    ".claude/settings.json": ("Bash", "Read", "Grep", "Edit", "Write", "NotebookEdit"),
    ".codex/hooks.json": ("Bash", "apply_patch"),
}
failures: list[str] = []
warnings: list[str] = []
loaded: dict[Path, object] = {}  # each JSON or TOML file is read, and reported, once


def rel(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def scalar(value: str) -> str | None:
    """A plain or fully quoted one-line YAML scalar; None for anything else."""
    if value[0] == '"':
        if not re.fullmatch(r'"(?:[^"\\]|\\.)*"', value):
            return None
        try:
            return json.loads(value)
        except ValueError:  # a YAML escape that JSON does not have
            return value[1:-1]
    if value[0] == "'":
        inner = value[1:-1]
        closed = len(value) >= 2 and value[-1] == "'" and "'" not in inner.replace("''", "")
        return inner.replace("''", "'") if closed else None
    if value[0] in "[{|>&*!%@`#" or value.startswith(("- ", "? ", ": ")):
        return None
    return None if ": " in value or " #" in value or value.endswith(":") else value


def frontmatter(path: Path, required: bool = True) -> tuple[dict, str]:
    """Frontmatter in the subset this script can validate without a YAML parser.

    Accepted: `key: scalar`, and `key:` followed by `- scalar` items or by a deeper-indented
    mapping of the same forms. One key holds a list or a mapping, never both, at one
    indentation. Anything else fails: both tools skip a file whose YAML does not parse.
    """
    text = path.read_text(encoding="utf-8")
    if not text.startswith("---\n"):
        if required:
            failures.append(f"{rel(path)}: no frontmatter")
        return {}, text
    match = re.match(r"---\n(.*?)\n---(?:\n|\Z)(.*)", text, re.S)
    if not match:
        failures.append(f"{rel(path)}: frontmatter has no closing ---")
        return {}, ""
    data: dict = {}
    frames = [(0, data)]  # (indentation, the mapping or list being filled), innermost last
    opened = None  # (mapping, key) of a `key:` with no value, until its first line says list or mapping
    for number, line in enumerate(match.group(1).splitlines(), start=2):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        indent = len(line) - len(line.lstrip(" "))
        pair = re.fullmatch(r" *([A-Za-z][\w-]*):(?:\s+(.*))?", line)
        item = re.fullmatch(r" *-\s+(.+)", line)
        value = ((pair.group(2) if pair else item.group(1)) or "").strip() if pair or item else ""
        why = None
        if not (pair or item):  # a tab in the indentation lands here too
            why = "not `key: value`, `key:` or `- item`"
        elif value and scalar(value) is None:
            why = "the value is not a plain or fully quoted one-line scalar (flow collection, block scalar or unbalanced quote)"
        else:
            if opened and (indent > frames[-1][0] or (item and indent == frames[-1][0])):
                mapping, key = opened
                mapping[key] = [] if item else {}
                frames.append((indent, mapping[key]))
            opened = None
            while frames[-1][0] > indent or (pair and len(frames) > 1 and frames[-1][0] == indent and isinstance(frames[-1][1], list)):
                frames.pop()  # back out to the collection this line belongs to
            level, target = frames[-1]
            if level != indent or isinstance(target, list) != bool(item):
                why = "it mixes list items and mapping entries under one key, or its indentation does not match the lines above"
            elif item:
                target.append(scalar(value))
            elif pair.group(1) in target:
                why = f"duplicate key '{pair.group(1)}'"
            else:
                target[pair.group(1)] = scalar(value) if value else []
                opened = None if value else (target, pair.group(1))
        if why:
            failures.append(f"{rel(path)}: frontmatter line {number}: {why}; outside the subset this check validates")
            break
    return data, match.group(2)


def load(path: Path):
    if path not in loaded:
        loaded[path] = None
        try:
            text = path.read_text(encoding="utf-8")
            loaded[path] = tomllib.loads(text) if path.suffix == ".toml" else json.loads(text)
        except FileNotFoundError:
            failures.append(f"{rel(path)}: missing")
        except ValueError as error:
            failures.append(f"{rel(path)}: does not parse: {error}")
    return loaded[path]


def check_skills() -> None:
    source, mirror, mods = ROOT / ".agents/skills", ROOT / ".claude/skills", ROOT / ".claude/mods"
    for skill in sorted(d for d in source.iterdir() if (d / "SKILL.md").is_file()):
        data, _ = frontmatter(skill / "SKILL.md")
        if not data.get("name") or not data.get("description"):
            failures.append(f"{rel(skill)}/SKILL.md: needs name and description (Codex skips it otherwise)")
        link = mirror / skill.name
        if not link.is_symlink() or link.resolve() != skill.resolve():
            failures.append(f".claude/skills/{skill.name}: not a symlink to {rel(skill)}")
    for entry in sorted(mirror.iterdir()) if mirror.is_dir() else []:
        if entry.name.startswith("."):
            continue
        homes = ((source / entry.name).resolve(), (mods / entry.name).resolve())
        if not entry.is_symlink():
            failures.append(f"{rel(entry)}: real file or directory; skills live in .agents/skills and mods in .claude/mods")
        elif not entry.exists() or entry.resolve() not in homes:
            failures.append(f"{rel(entry)}: must be a symlink to .agents/skills/{entry.name} or .claude/mods/{entry.name}")


def check_mods() -> None:
    """A mod is a plugin with a hooks module; Claude Code loads it through its .claude/skills symlink."""
    for mod in sorted(d for d in (ROOT / ".claude/mods").glob("*") if d.is_dir()):
        manifest, hooks = load(mod / ".claude-plugin/plugin.json"), load(mod / "hooks/hooks.json")
        if manifest is not None and manifest.get("name") != mod.name:
            failures.append(f"{rel(mod)}: plugin.json name must be '{mod.name}', the directory name")
        modules = hooks.get("modules") if isinstance(hooks, dict) else None
        modules = modules if isinstance(modules, list) else []
        if hooks is not None and not modules:
            failures.append(f"{rel(mod)}/hooks/hooks.json: lists no modules, so the plugin is not a mod")
        for module in modules:
            if not (mod / "hooks" / str(module)).is_file():
                failures.append(f"{rel(mod)}/hooks/hooks.json: module {module} does not exist")
        link = ROOT / ".claude/skills" / mod.name
        if not link.is_symlink() or link.resolve() != mod.resolve():
            failures.append(f".claude/skills/{mod.name}: not a symlink to {rel(mod)}, so Claude Code does not load the mod")


def check_statusline(line) -> None:  # a project statusLine replaces the user's own, so it must hand the payload on
    command = line.get("command", "") if isinstance(line, dict) else ""
    if line is None:
        return
    if ".claude/statusline.sh" not in command or not os.access(STATUSLINE, os.X_OK):
        failures.append(".claude/settings.json: statusLine must run .claude/statusline.sh, which must be executable")
    elif not shutil.which("jq"):
        warnings.append(f"{rel(STATUSLINE)}: jq is not installed, so the status line was not exercised")
    else:
        payload = json.dumps({"model": {"id": "claude-opus-5-5"}, "workspace": {"project_dir": str(ROOT)}, "context_window": {"used_percentage": 42.7}})
        with tempfile.TemporaryDirectory() as tmp:
            def run(argv: list[str], text: str) -> tuple[int, list[str], str]:
                env = {**os.environ, "CLAUDE_CONFIG_DIR": tmp, "POURYAKIT_STATUSLINE": ""}
                done = subprocess.run(argv, input=text, capture_output=True, text=True, timeout=20, env=env, cwd=tmp)
                return done.returncode, done.stdout.splitlines(), done.stderr
            wired, seen = ["sh", "-c", command], Path(tmp) / "seen"
            code, out, _ = run(wired, payload)
            if code or len(out) != 1 or "opus-5-5" not in out[0] or "ctx 42%" not in out[0]:
                failures.append(f".claude/settings.json: statusLine printed {out} for a mock payload, not one line with the model and ctx")
            if any(run(argv, bad) != (0, [], "") for bad in ("", "not json") for argv in (wired, [str(STATUSLINE)])):
                failures.append(f"{rel(STATUSLINE)}: an empty or non-JSON payload must give exit 0 and no output")
            stub = f"cat > {shlex.quote(str(seen))}; echo user-line; exit 3"
            (Path(tmp) / "settings.json").write_text(json.dumps({"statusLine": {"type": "command", "command": stub}}))
            code, out, _ = run(wired, payload)
            if code or len(out) != 2 or out[0] != "user-line" or not seen.is_file() or seen.read_text().strip() != payload:
                failures.append(f"{rel(STATUSLINE)}: must pass the payload to the user's own statusLine command and print its output first")


def check_claude_settings() -> None:
    settings = load(ROOT / ".claude/settings.json") or {}
    check_statusline(settings.get("statusLine"))
    styles = set()
    for style in sorted((ROOT / ".claude/output-styles").glob("*.md")):
        data, _ = frontmatter(style, required=False)
        if extra := sorted(set(data) - OUTPUT_STYLE_KEYS):
            failures.append(f"{rel(style)}: frontmatter keys {extra}; an output style reads only {sorted(OUTPUT_STYLE_KEYS)}")
        styles.add(data.get("name") or style.stem)
    chosen = settings.get("outputStyle")
    if chosen is not None and chosen not in styles:
        failures.append(f".claude/settings.json: outputStyle '{chosen}' names no style in .claude/output-styles")
    worktree = settings.get("worktree")
    if not isinstance(worktree, dict) or worktree.get("baseRef") != "head":  # code.claude.com/docs/en/settings-reference#worktree-baseref
        failures.append(".claude/settings.json: worktree.baseRef must be \"head\", so a subagent worktree starts at the session's HEAD, not origin/HEAD")


def check_agents() -> None:
    claude = {p.stem: p for p in (ROOT / ".claude/agents").glob("*.md")}
    codex = {p.stem: p for p in (ROOT / ".codex/agents").glob("*.toml")}
    if not claude and not codex:
        failures.append("no role files in .claude/agents or .codex/agents")
    for stem in sorted(claude.keys() | codex.keys()):
        data, body = frontmatter(claude[stem]) if stem in claude else ({}, "")
        toml = load(codex[stem]) if stem in codex else None
        if unknown := sorted(set(data) - AGENT_KEYS):
            failures.append(f"{rel(claude[stem])}: undocumented frontmatter keys {unknown}")
        if stem not in claude or stem not in codex:
            missing = f".claude/agents/{stem}.md" if stem not in claude else f".codex/agents/{stem}.toml"
            failures.append(f"role '{stem}': {missing} is missing; every role is a Claude + Codex pair")
            continue
        if toml is None:
            continue
        for key in ("name", "description"):
            if not data.get(key) or data.get(key) != toml.get(key):
                failures.append(f"role '{stem}': '{key}' differs between the .md and the .toml")
        if data.get("name") != stem:
            failures.append(f"{rel(claude[stem])}: name '{data.get('name')}' does not match the file name")
        if body.strip() != str(toml.get("developer_instructions", "")).strip():
            failures.append(f"role '{stem}': .md body and .toml developer_instructions differ")


def matches(matcher: str, tool: str) -> bool:
    """A hook matcher: empty or * for everything, a | or , list of exact names, else an unanchored regex."""
    if matcher in ("", "*"):
        return True
    if re.fullmatch(r"[\w\-, |]+", matcher):
        return tool in re.split(r"\s*[|,]\s*", matcher.strip())
    try:
        return bool(re.search(matcher, tool))
    except re.error:
        return False


def hook_scripts(handler: dict) -> list[str]:
    """Script paths a hook handler names. Words keep their boundaries and the repository
    placeholder is expanded only after splitting, so a checkout path with spaces stays one path."""
    placeholders = ("${CLAUDE_PROJECT_DIR}", "$CLAUDE_PROJECT_DIR", "$(git rev-parse --show-toplevel)")
    mark = "\ue000"  # stands in for the repository root while the command is split into words
    command = str(handler.get("command", ""))
    for placeholder in placeholders:
        command = command.replace(placeholder, mark)
    if "args" in handler:
        words = [command, *map(str, handler["args"])]
    else:
        try:
            words = shlex.split(command)
        except ValueError:
            words = command.split()
    for placeholder in (mark, *placeholders):
        words = [word.replace(placeholder, str(ROOT)) for word in words]
    return [str(ROOT / word) for word in words if re.search(r"\.(?:py|sh|mjs|js)$", word)]


def check_hook_commands(file: str) -> None:
    config = load(ROOT / file) or {}
    guarded: set[str] = set()
    for event, groups in config.get("hooks", {}).items():
        for group in groups:
            for handler in group.get("hooks", []):
                for script in hook_scripts(handler):
                    if not Path(script).is_file():
                        failures.append(f"{file}: hook script {script} does not exist")
                    elif not os.access(script, os.X_OK):
                        failures.append(f"{file}: hook script {script} is not executable")
                    elif event == "PreToolUse" and handler.get("type") == "command" and Path(script).resolve() == GUARD.resolve():
                        guarded |= {tool for tool in GUARDED_TOOLS[file] if matches(group.get("matcher") or "", tool)}
    if unguarded := [tool for tool in GUARDED_TOOLS[file] if tool not in guarded]:
        failures.append(f"{file}: no PreToolUse command hook runs {rel(GUARD)} for {', '.join(unguarded)}")


def guard_decision(payload: dict) -> tuple[str, str]:
    run = subprocess.run(
        [sys.executable, str(GUARD)], input=json.dumps(payload), capture_output=True, text=True, timeout=10
    )
    try:
        out = json.loads(run.stdout).get("hookSpecificOutput", {})
        return out.get("permissionDecision", "allow"), out.get("permissionDecisionReason", "")
    except (ValueError, AttributeError):
        return f"no JSON (exit {run.returncode})", ""


def check_guard() -> None:
    if not GUARD.is_file():
        failures.append(f"{rel(GUARD)}: missing")
        return
    # The mod recognises this guard's denies by the fixed start of their reason, so both files spell it alike.
    spelled = {p: re.search(r"MARKER = (['\"])(.+?)\1", p.read_text(encoding="utf-8")) for p in (GUARD, MOD_HOOKS) if p.is_file()}
    marker = spelled[GUARD].group(2) if spelled[GUARD] else ""
    if not marker or any(not found or found.group(2) != marker for found in spelled.values()):
        failures.append(f"guard: {rel(GUARD)} and {rel(MOD_HOOKS)} must define the same MARKER for deny reasons")
    outside = Path.home() / "reference-project/src/A.cs"  # never a writable root, wherever the repo lives

    def patch(path) -> dict:
        return {"tool_name": "apply_patch", "tool_input": {"command": f"*** Begin Patch\n*** Update File: {path}\n*** End Patch"}}

    def shell(command: str) -> dict:  # Claude and Codex both send Bash as tool_input.command
        return {"tool_name": "Bash", "tool_input": {"command": command}}

    def tool(name: str, **tool_input) -> dict:
        return {"tool_name": name, "tool_input": tool_input}

    scratch = tempfile.TemporaryDirectory(prefix="kit-guard-")
    links = Path(scratch.name)  # a secret is judged by the name asked for and by its link target
    (links / "production").write_text("")
    (links / "dev.pem").write_text("")
    (links / ".env").symlink_to(links / "production")
    (links / "notes.txt").symlink_to(links / "dev.pem")
    (links / "config'").symlink_to(links / "dev.pem")  # a quote in a file name is part of the name
    cases = [
        ("deny", "Claude write outside the working tree", tool("Write", file_path=str(outside))),
        ("allow", "Claude write inside the working tree", tool("Write", file_path=str(ROOT / "src/A.cs"))),
        ("deny", "Codex patch outside the working tree", patch(outside)),
        ("allow", "Codex patch inside the working tree", patch("src/A.cs")),
        ("deny", "Claude read of .env", tool("Read", file_path=str(ROOT / ".env"))),
        ("allow", "Claude read of .env.example", tool("Read", file_path=str(ROOT / ".env.example"))),
        ("deny", "Codex patch of a .pem file", patch("certs/dev.pem")),
        ("deny", "Claude read of .env that is a symlink to another name", tool("Read", file_path=str(links / ".env"))),
        ("deny", "Claude read of a symlink that leads to a .pem file", tool("Read", file_path=str(links / "notes.txt"))),
        ("deny", "Codex patch of .env that is a symlink to another name", patch(links / ".env")),
        ("deny", "Claude read of a name ending in a quote that leads to a .pem file", tool("Read", file_path=str(links / "config'"))),
        ("deny", "Claude Grep on .env", tool("Grep", pattern="KEY", path=str(ROOT / ".env"))),
        ("deny", "Claude Grep with a .env glob", tool("Grep", pattern="KEY", glob=".env*")),
        ("deny", "Claude Grep with a leading-wildcard .env glob", tool("Grep", pattern="KEY", glob="*.env*")),
        ("deny", "Claude Grep with a .env glob under any directory", tool("Grep", pattern="KEY", glob="**/.env")),
        ("allow", "Claude Grep on a directory", tool("Grep", pattern="KEY", path=str(ROOT / "docs"), glob="*.md")),
        ("allow", "Claude Grep with a source glob", tool("Grep", pattern="KEY", glob="**/*.cs")),
        ("allow", "Claude Grep with a glob that only resembles .env", tool("Grep", pattern="KEY", glob="*.envelope.json")),
        ("allow", "a path with an embedded NUL does not crash the guard", tool("Read", file_path="a\x00b")),
    ]
    # Shell rules apply at command position only: quoted or backslash-escaped text is data.
    allowed = [
        r"find . \( -name '*.cs' -o -name '*.md' \) -print",
        r"echo a\;b",
        r"echo \> notes",
        r"ls My\ Folder",
        "rm -rf /tmp/kit-test/out >/dev/null 2>&1",
        "echo '$(rm -rf build)'",
        'test "$a" ">" "$b"',
        "dotnet test 2>&1 | tail -n 20",
        'git commit -m "stop using rm -rf in the script"',
        "git rm -r docs/old",
        "docker run --rm img",
        "rm -- notes.txt",
        "echo notes.txt | xargs rm -f",
        # The one recursive delete allowed: a direct rm of literal absolute temp paths, options aside.
        "rm -rf /tmp/kit-test/out",
        "rm -r -f /tmp/kit-test/a /tmp/kit-test/b",
        "sudo rm -rf /tmp/kit-test/out",
        "git push --force-with-lease origin feat/x",
        "git push -u origin feat/x",
        'bash -lc "dotnet build && dotnet test"',
        "dotnet test \\\n  --filter Name",  # a backslash-newline joins what surrounds it
        "echo 'a\\\nb'",
        # After a wrapper every guarded word is judged; these name one without running a blocked command.
        "sudo -u git ls",
        "env -u git git status",
    ]
    denied = [
        r"find . -type f \( -name '*.tmp' -o -name '*.bak' \) -delete",
        r"find . -type f \( -name '*.tmp' -o -name '*.bak' \) -exec rm -rf {} \;",
        r"find . -name '*.tmp' -delete \;",
        "2>/dev/null rm -rf bin",
        ">/dev/null rm -rf bin",
        "2>&1 find . -delete",
        ">/dev/null git push -f origin main",
        "rm -rf bin 2>/dev/null",
        'cat notes >"$(rm -rf out)"',
        "bash -c 'echo `rm -rf out`'",
        "rm -rf bin obj",
        r"printf '%s\n' src | xargs rm -rf /tmp/kit-cache",  # xargs appends operands the guard cannot see
        "rm -rf -- /tmp/kit-scratch -cache",  # after --, -cache is a directory in the checkout
        "rm -rf /tmp/kit-scratch ./-cache",
        "cd src && rm -rf bin",
        "sudo rm -rf build",
        "env -u git rm -rf build",  # `git` is the value of -u, not the command
        "nohup nice rm -rf build",
        "find . -name '*.tmp' | xargs rm -r",
        "FOO=1 rm -r out",
        "git clean -fd",
        "git -C . push --force origin main",
        "git push origin +main",
        "git push origin :old-branch",
        'rm -rf "$DIR/out"',
        "rm -rf /tmp/../Users/x",
        "rm -\\\nrf ./build",  # the shell runs rm -rf ./build
        "r\\\nm -rf build",
        "git push --for\\\nce origin main",
        'echo "$(rm -rf out)"',
        'bash -lc "rm -rf out"',
        "echo 'unbalanced quote ; rm -rf out",
    ]
    cases += [("allow", f"shell: {command}", shell(command)) for command in allowed]
    cases += [("deny", f"shell: {command}", shell(command)) for command in denied]
    for expected, label, payload in cases:
        decision, reason = guard_decision({**payload, "cwd": str(ROOT), "hook_event_name": "PreToolUse"})
        if decision != expected:
            failures.append(f"guard: {label}: expected {expected}, got {decision}")
        elif decision == "deny" and not reason.startswith(marker):
            failures.append(f"guard: {label}: the deny reason does not start with '{marker}'")
    scratch.cleanup()


def glob_regex(pattern: str) -> re.Pattern:
    out, i = "", 0
    while i < len(pattern):
        close = pattern.find("}", i)
        if pattern.startswith("**/", i):
            out, i = out + "(?:.*/)?", i + 3
        elif pattern.startswith("**", i):
            out, i = out + ".*", i + 2
        elif pattern[i] == "*":
            out, i = out + "[^/]*", i + 1
        elif pattern[i] == "?":
            out, i = out + "[^/]", i + 1
        elif pattern[i] == "{" and close != -1:
            out, i = out + "(?:" + "|".join(map(re.escape, pattern[i + 1 : close].split(","))) + ")", close + 1
        else:
            out, i = out + re.escape(pattern[i]), i + 1
    return re.compile(out + r"\Z")


def check_instructions_and_rules() -> None:
    claude_md, agents_md = ROOT / "CLAUDE.md", ROOT / "AGENTS.md"
    tokens = claude_md.read_text(encoding="utf-8").split() if claude_md.is_file() else []
    if not tokens or tokens[0] != "@AGENTS.md":
        failures.append("CLAUDE.md: must start with @AGENTS.md")
    router = agents_md.read_text(encoding="utf-8") if agents_md.is_file() else ""
    if len(router.encode()) > AGENTS_MD_LIMIT:
        failures.append(f"AGENTS.md: {len(router.encode())} bytes, over Codex's {AGENTS_MD_LIMIT}-byte limit")
    listed = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard"], cwd=ROOT, capture_output=True, text=True
    ).stdout.splitlines()
    for rule in sorted((ROOT / ".claude/rules").glob("**/*.md")):
        data, _ = frontmatter(rule, required=False)
        if extra := sorted(set(data) - {"paths"}):
            failures.append(f"{rel(rule)}: frontmatter keys {extra}; Claude Code reads only 'paths'")
        if rel(rule) not in router:
            failures.append(f"{rel(rule)}: not listed in the AGENTS.md rule router")
        paths = data.get("paths", [])
        for pattern in paths if isinstance(paths, list) else [p.strip() for p in str(paths).split(",")]:
            if not any(glob_regex(pattern).match(name) for name in listed):
                warnings.append(f"{rel(rule)}: paths glob '{pattern}' matches no file yet")
    for workflow in sorted((ROOT / ".claude/workflows").glob("*.js")):
        lines = workflow.read_text(encoding="utf-8").splitlines()
        code = [line for line in lines if line.strip() and not line.lstrip().startswith("//")]
        if not code or not code[0].startswith("export const meta"):
            failures.append(f"{rel(workflow)}: first statement must be `export const meta`")


def check_doc_pointers() -> None:
    """A doc under docs/agents/ is reached only through a pointer in a file an agent loads, so each needs one and each pointer must resolve."""
    sources = [ROOT / "AGENTS.md", *sorted((ROOT / ".claude").glob("rules/*.md")), *sorted((ROOT / ".claude").glob("output-styles/*.md")), *sorted((ROOT / ".claude").glob("agents/*.md"))]
    named = {name: rel(source) for source in sources if source.is_file() for name in re.findall(r"docs/agents/[\w.-]+\.md", source.read_text(encoding="utf-8"))}
    for name, source in sorted(named.items()):
        if not (ROOT / name).is_file():
            failures.append(f"{source}: points to {name}, which does not exist")
    for doc in sorted((ROOT / "docs/agents").glob("*.md")):
        if rel(doc) not in named:
            failures.append(f"{rel(doc)}: no pointer to it in AGENTS.md, a rule, the output style or a role file, so no agent reaches it")


def check_conflict_markers() -> None:
    """No tracked text file holds a merge conflict marker. `=======` alone is a Markdown setext heading, so it is not one."""
    listed = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout.decode().split("\0")
    for name in sorted(set(filter(None, listed))):
        path = ROOT / name
        if path.is_symlink() or not path.is_file():
            continue
        data = path.read_bytes()
        if b"\0" in data:  # binary
            continue
        for number, line in enumerate(data.decode("utf-8", "replace").splitlines(), start=1):
            if line.startswith(("<" * 7 + " ", ">" * 7 + " ")):
                failures.append(f"{name}:{number}: merge conflict marker")


def main() -> int:
    check_conflict_markers()
    check_skills()
    check_mods()
    check_agents()
    check_claude_settings()
    for file in (".claude/settings.json", ".codex/hooks.json"):
        check_hook_commands(file)
    for file in (".codex/config.toml", "skills-lock.json"):
        load(ROOT / file)
    check_guard()
    check_instructions_and_rules()
    check_doc_pointers()
    for message in warnings:
        print(f"WARN  {message}")
    for message in failures:
        print(f"FAIL  {message}")
    print(f"check-ai-setup: {len(failures)} failure(s), {len(warnings)} warning(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
