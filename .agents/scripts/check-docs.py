#!/usr/bin/env python3
"""Checks the repository's Markdown, and the source ranges its diagrams cite, for the drift a script can catch; exits 1
on any failure.

Usage: python3 .agents/scripts/check-docs.py [--staged | --fences]

It reads every Markdown file git tracks or would track (`git ls-files --cached --others --exclude-standard`), with
fenced code and frontmatter blanked, except the vendored skills that `skills-lock.json` pins: their text is upstream's.
The docs site's MDX pages, `website/content/**/*.mdx`, are read the same way for the Names rule alone: their imports,
JSX and site routes (`/docs/...`) are no Markdown the links, code-span, table or test-pointer rules could judge.

- Names: a backticked PascalCase identifier in the Kit skill, `README.md`, `docs/architecture/`, a top-level
  `docs/*.md`, a package's `src/<Id>/README.md` (exactly one segment between `src/` and the file, so a template's
  `src/PouryaKit.Templates/content/<x>/README.md` stays out) or a docs site page `website/content/**/*.mdx` (no other
  `.mdx`, and nothing else under `website/`) must name an identifier of the code (`src/`, `samples/`, `tests/`,
  `scripts/`), or be in ALLOWED.
  Only code-like spans count: a span holding a path or citation (`/`, `@`), a header, YAML or config key (`Name:`,
  `Name=`) or two adjacent words that are not C# keywords (prose, a command line) is skipped, and so is a dotted
  name rooted in a namespace of ROOTS or at an ALLOWED entry. Out of scope by design: `docs/adr/` (records that name
  renamed and removed symbols on purpose) and `docs/foundation-review.md` (the plan).
- Links: every relative link and `#anchor` resolves in the working tree, inline or through a reference definition
  (`[id]: target`), with GitHub's heading slugs and duplicates numbered; a `[text][id]` with no definition fails.
- Code spans: a span longer than 20 characters whose text starts or ends with a space fails, after CommonMark's
  padding is stripped (one space from each end of a span padded on both sides). That is a backtick
  paired with the wrong partner: a sentence inserted inside a span (6ecf342) turns the prose between the spans after
  it into spans. Length or a sentence boundary alone cannot tell: test names, commands and quoted Kit messages run
  past 120 characters and hold sentences, while the spans of 6ecf342 stay under 90. Backticks pair per paragraph,
  as CommonMark pairs them, so a span may wrap.
- Tables: a run of lines starting with `|` is a table only when its second line is the separator, so a row cut off
  from its table, or a heading or paragraph put inside one, fails.
- Union duplicates: in a file `.gitattributes` merges with `merge=union`, a line twice or two lines linking the same
  target fails. A union merge keeps both sides of a line both branches changed.
- Test pointers: in `docs/acceptance.md`, a span that is exactly `Class.Method` with a class ending in `Tests` must
  name a declared test: a method that the body of a class of that name declares,
  in a `.cs` file under `tests/` or the templates' generated tests, or in a `test_*.py`. Python is read with `ast`;
  in C#, comments and string literals are no code, and what sits inside a member's own braces (a method body, a
  nested type) is no declaration of the class. A pointer qualified by its namespace, `Namespace.Class.Method` or
  `Namespace.Class`, must name a class of that name in a file whose first namespace is that one, and the method when
  it names one; a span that is a test file's namespace (`PouryaKit.ArchitectureTests`) names a project, not a test.
  A span that reads both ways, `ArchitectureTests.LayerTests`, names the class when a file of that namespace declares it.
  A renamed test is drift to fix (`git log -S<Method>` finds the new name). `docs/adr/` stays out: it keeps renamed
  names on purpose.
- Kit paths: a path the docs cite into the Kit must name a file or folder that git tracks or would track and the
  working tree holds: each span that is a path into the Kit's packages (`src/PouryaKit.*`), `samples/` or `tests/`
  in a top-level `docs/*.md`, the plan and the acceptance map among them. A `:line` suffix is not part of the
  path, and a path holding `*` is a glob that must match one. Out of scope by design: an upstream repository's path,
  which the plan cites as `repository@commit path` or as a path outside the Kit's packages; the test project of a
  solution generated from the templates (`tests/acme-shop.Tests`), which exists only during a `validate.py` run; and
  `docs/adr/`, dated records. A moved file is cited at its new path.
- Diagram sources: each Archify diagram source, `docs/architecture/diagrams/*.json`, pins `meta.repository.revision`,
  and every entry of a `sources` list in it (`path`, `line`, `end_line`, one line when that is absent) must still
  describe the code: the revision is in HEAD's history (during a merge, one reachable from `MERGE_HEAD` counts), the
  path names a file at HEAD, `line` and `end_line` are in order within its lines, and no hunk of
  `git diff <revision> HEAD` starts at or above the range's last line: a change below a cited range leaves its lines
  where they were, while one inside or above it rewrites or moves them, and a range cannot tell the two apart. The
  HTML pages are not read.

`--staged` reads the diagram sources and their cited files from the index, and compares those files with the pinned
revision (the default compares HEAD). A cited file staged together with its diagram passes if the ranges fit the indexed
file, so the repair path is two commits: the code and the diagram's ranges and labels first, then the diagram re-pinned
to that commit. A change at or above a cited range staged without its diagram is refused; a change below every cited
range passes. The other rules still read the working tree: the hook refuses docs and AI-setup paths staged in part, but
a source file staged in part can make those rules see different names or test declarations from the commit. CI's check
on the committed tree is the gate.

`--fences` only prints: for each Markdown file changed against HEAD, whether its fenced blocks changed, so an
implementer knows whether the skill's snippet harness must run.
"""

from __future__ import annotations

import ast
import fnmatch
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

ROOT = Path(__file__).resolve().parents[2]
CODE_DIRS = ("src/", "samples/", "tests/", "scripts/")
CODE_SUFFIXES = (".cs", ".csproj", ".json", ".py", ".props", ".slnx")
NAMES_SKIPPED = {"docs/foundation-review.md"}
PACKAGE_README = re.compile(r"src/[^/]+/README\.md")  # one segment between src/ and the file: a package, never a template's content
SITE_PAGE = re.compile(r"website/content/.+\.mdx")  # a docs site page, which only the Names rule reads
# Namespaces of third-party and framework code; a dotted name rooted in one is not the Kit's.
ROOTS = {
    "System", "Microsoft", "Wolverine", "WolverineFx", "JasperFx", "Npgsql", "Testcontainers", "Asp", "Serilog",
    "OpenTelemetry", "StackExchange", "RabbitMQ", "FluentValidation", "Dapper", "Polly", "Scalar", "Newtonsoft",
}
# Names the docs use on purpose that no Kit code declares, one per line with the reason. A dotted entry covers
# that member; a dotted name rooted at an entry is skipped whole.
ALLOWED = {
    "IProductReader": "illustrative: the skill's example of a read port",
    "ReserveStockCommand": "illustrative: the skill's example of a command sent from a handler",
    "IReportExporter": "illustrative: the skill's example of an application interface",
    "AuditHandler": "illustrative: the skill's example of a second Integration Event Handler",
    "SharedFeatures": "illustrative: the skill's example of a second Feature assembly",
    "IntentionalHostedConsumerFailure": "illustrative: the undefined call a temporary CI proof added (docs/ci.md)",
    "GitHub": "the NuGet source key a consumer's NuGet.Config declares (docs/private-packages.md)",
    "AssemblyVersion": "MSBuild property",
    "FileVersion": "MSBuild property",
    "AllowAnyOrigin": "ASP.NET Core CORS policy builder method",
    "MapPatch": "ASP.NET Core endpoint routing method",
    "Unhealthy": "ASP.NET Core health check status, the text /health answers",
    "MetadataAddress": "ASP.NET Core JwtBearerOptions property",
    "OwnsMany": "EF Core owned-type mapping method",
    "RepositoryBase": "Ardalis.Specification base class",
    "ExecutionContext": ".NET System.Threading type",
    "DateTimeOffset.Now": ".NET clock property",
    "TypeLoadMode.Dynamic": "JasperFx code generation mode",
    "DurableLocalQueue": "Wolverine type",
    "IMessageStore": "Wolverine interface",
}
ACCEPTANCE = "docs/acceptance.md"
TEST_SOURCE = re.compile(r"(?:tests/|src/PouryaKit\.Templates/content/.+/tests/).+\.cs|(?:.+/)?test_\w+\.py")
TEST_POINTER = re.compile(r"(\w+Tests)\.(?!cs$)(\w+)")  # `CatalogTests.cs` names a file, not a test
QUALIFIED_POINTER = re.compile(r"(\w+(?:\.\w+)*\.\w+Tests)(?:\.(?!cs$)(\w+))?")  # `Namespace.Class.Method`, or the class
# C# text that is no code: a comment, a raw, a verbatim or an ordinary string literal, a character literal.
CS_NOT_CODE = re.compile("|".join((
    r"//[^\n]*", r"/\*.*?\*/",
    r'\$*("{3,}).*?\1', r'\$?@\$?"(?:[^"]|"")*"', r'\$?"(?:[^"\\\n]|\\.)*"',
    r"'(?:[^'\\\n]|\\.)+'",
)), re.S)
CITED_PATH = re.compile(r"([\w.-]+/[^\s:]*)(?::[\d,-]+)?")  # a span that is a repository path, maybe with a `:line`
KIT_ROOTS = ("src/PouryaKit.", "samples/", "tests/")  # the Kit's packages, the Proving Sample and the tests
# The test project of a solution that `validate.py` generates from the templates under one of these names exists
# only during the run. A generated `src/` project is no Kit package, so the rule never reads one.
GENERATED = re.compile(r"tests/(?:acme[-_]shop|Acme\.Shop)\.")
KEYWORDS = set("""
    abstract as base bool break byte case catch char checked class const continue decimal default delegate do double
    else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal
    is lock long namespace new null object operator out override params private protected public readonly ref return
    sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked
    unsafe ushort using virtual void volatile while add alias and args ascending async await by descending dynamic
    equals extension field file from get global group init into join let managed nameof nint not notnull nuint on or
    orderby partial record remove required scoped select set unmanaged value var when where with yield
""".split())
STRING = re.compile(r'\$?@?"(?:[^"\\]|\\.)*"')
PASCAL = re.compile(r"[A-Z][A-Za-z0-9]*[a-z][A-Za-z0-9]*")
FENCE = re.compile(r" {0,3}(`{3,}|~{3,})(.*)")
BLOCK_START = re.compile(r"\s{0,3}#{1,6}(?:\s|$)|\s*(?:[-*+]|\d{1,9}[.)])\s|\s*>|\s*\|")
ONE_LINE_BLOCK = re.compile(r"\s{0,3}#{1,6}(?:\s|$)|\s*\|")
TABLE_ROW = re.compile(r"\s*\|")
SEPARATOR = re.compile(r"\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*")
# The destination may hold one level of balanced parentheses; the title, when present, is quoted with "", '' or ().
LINK = re.compile(r"\]\(\s*<?((?:[^()\s<>]|\([^()\s]*\))+)>?(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*\)")
DEFINITION = re.compile(r" {0,3}\[([^\]]+)\]:\s*<?([^\s<>]+)>?(?:\s+(?:\"[^\"]*\"|'[^']*'|\([^)]*\)))?\s*")
REFERENCE = re.compile(r"\[([^\]]+)\]\[([^\]]*)\]")
failures: list[str] = []


def git(*args: str, stdin: str | None = None) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, input=stdin, capture_output=True, text=True, check=True).stdout


def listed() -> list[str]:
    return sorted(set(git("ls-files", "--cached", "--others", "--exclude-standard").splitlines()))


def vendored() -> tuple[str, ...]:
    lock = ROOT / "skills-lock.json"
    skills = json.loads(lock.read_text(encoding="utf-8")).get("skills", {}) if lock.is_file() else {}
    return tuple(f".agents/skills/{name}/" for name in skills)


def blank_code(text: str) -> tuple[list[str], list[tuple[str, str]]]:
    """The file's lines with frontmatter and fenced code blanked, and each fenced block as (info, content)."""
    lines, blocks, fence = text.split("\n"), [], None
    if lines[0] == "---" and "---" in lines[1:]:
        end = lines.index("---", 1)
        lines[: end + 1] = [""] * (end + 1)
    for number, line in enumerate(lines):
        opening = FENCE.match(line)
        if fence is None and opening:
            fence, info, body = opening.group(1), opening.group(2).strip(), []
        elif fence is not None:
            if re.fullmatch(r" {0,3}" + re.escape(fence[0]) + "{" + str(len(fence)) + r",}\s*", line):
                blocks.append((info, "\n".join(body)))
                fence = None
            else:
                body.append(line)
        else:
            continue
        lines[number] = ""
    if fence is not None:  # an unclosed fence runs to the end of the file
        blocks.append((info, "\n".join(body)))
    return lines, blocks


def inline_blocks(lines: list[str]):
    """(first line index, text) of each run of lines inline content can wrap across: a paragraph or list item.
    A heading or table row is a block of its own."""
    start, run = 0, []
    for number, line in enumerate(lines + [""]):
        if run and (not line.strip() or BLOCK_START.match(line) or ONE_LINE_BLOCK.match(run[-1])):
            yield start, "\n".join(run)
            run = []
        if line.strip():
            start = start if run else number
            run.append(line)


def code_spans(text: str):
    """(start, end, content) of each CommonMark code span: a backtick run opens one, the next run of the same
    length closes it, and a run nothing closes is literal. Line breaks become spaces."""
    runs = [match.span() for match in re.finditer(r"`+", text)]
    i = 0
    while i < len(runs):
        start, end = runs[i]
        close = next((j for j in range(i + 1, len(runs)) if runs[j][1] - runs[j][0] == end - start), None)
        if close is None:
            i += 1
            continue
        content = re.sub(r"\n\s*", " ", text[end : runs[close][0]])
        if content.startswith(" ") and content.endswith(" ") and content.strip(" "):
            content = content[1:-1]  # CommonMark's padding, which lets a span start or end with a backtick
        yield start, runs[close][1], content
        i = close + 1


class Doc:
    def __init__(self, path: str):
        self.lines = blank_code((ROOT / path).read_text(encoding="utf-8"))[0]
        self.blocks = list(inline_blocks(self.lines))

    def spans(self):
        """(line number, content) of every code span."""
        for first, text in self.blocks:
            for start, _, content in code_spans(text):
                yield first + 1 + text.count("\n", 0, start), content

    def prose(self):
        """(first line number, text) of each block with its code spans blanked, where links are matched."""
        for first, text in self.blocks:
            for start, end, _ in reversed(list(code_spans(text))):
                text = text[:start] + re.sub(r"[^\n]", " ", text[start:end]) + text[end:]
            yield first + 1, text


def code_like(span: str) -> bool:
    """False for a path, citation, header, config key or prose; `span` has its string literals emptied."""
    if re.search(r"[/@]", span) or re.search(r"\b[A-Z][\w-]*[:=](?![:=>])", span):
        return False
    words = list(re.finditer(r"[A-Za-z_]\w*", span))
    return not any(
        span[a.end() : b.start()].isspace() and a.group() not in KEYWORDS and b.group() not in KEYWORDS
        for a, b in zip(words, words[1:])
    )


def unknown_names(span: str, corpus: set[str]) -> list[str]:
    span = STRING.sub('""', span)
    if not code_like(span):
        return []
    found = []
    for chain in re.findall(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", span):
        parts = chain.split(".")
        if parts[0] in ROOTS or any(".".join(parts[:n]) in ALLOWED for n in range(1, len(parts) + 1)):
            continue
        found += [part for part in parts if PASCAL.fullmatch(part) and part not in corpus and part not in ALLOWED]
    return found


def names_target(path: str) -> bool:
    top_doc = path.startswith("docs/") and path.count("/") == 1 and path not in NAMES_SKIPPED
    return (path.startswith((".agents/skills/pourya-kit/", "docs/architecture/")) or path == "README.md" or top_doc
            or bool(PACKAGE_README.fullmatch(path) or SITE_PAGE.fullmatch(path)))


def check_names(docs: dict[str, Doc], files: list[str]) -> None:
    code = [f for f in files if f.startswith(CODE_DIRS) and f.endswith(CODE_SUFFIXES) and (ROOT / f).is_file()]
    corpus = {word for f in code for word in re.findall(r"[A-Za-z_]\w*", (ROOT / f).read_text(encoding="utf-8", errors="ignore"))}
    for path, doc in docs.items():
        if not names_target(path):
            continue
        reported = set()
        for number, span in doc.spans():
            for name in unknown_names(span, corpus):
                if name not in reported:
                    reported.add(name)
                    failures.append(f"{path}:{number}: `{name}` names nothing in the code (in `{span[:80]}`); fix the name, or add it to ALLOWED with a reason")


def slug(heading: str) -> str:
    """GitHub's heading id: the rendered text lowercased, punctuation but `-` and `_` dropped, each space a `-`."""
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    text = re.sub(r"[`*]|(?<!\w)_+|_+(?!\w)", "", text)
    return re.sub(r"[^\w\- ]", "", text.strip().lower()).replace(" ", "-")


def anchors(doc: Doc) -> set[str]:
    """Every heading's id. As github-slugger does, a taken id gets the next free `-<n>` suffix, so `A`, `A`, `A-1`
    give a, a-1 and a-1-1."""
    taken: dict[str, int] = {}
    for line in doc.lines:
        heading = re.match(r" {0,3}#{1,6}\s+(.*?)(?:\s+#+)?\s*$", line)
        if heading:
            base = found = slug(heading.group(1))
            while found in taken:
                taken[base] += 1
                found = f"{base}-{taken[base]}"
            taken[found] = 0
    return set(taken)


def label(text: str) -> str:
    """A reference label as CommonMark matches it: case and runs of whitespace do not count."""
    return " ".join(text.split()).casefold()


def check_links(docs: dict[str, Doc], files: list[str]) -> None:
    tracked = set(files) | {str(Path(f).parents[i]) for f in files for i in range(len(Path(f).parents) - 1)}
    cached: dict[str, set[str]] = {}

    def check(path: str, number: int, target: str) -> None:
        if re.match(r"[A-Za-z][A-Za-z0-9+.-]*:", target):
            return
        part, _, anchor = target.partition("#")
        part, anchor = unquote(part), unquote(anchor)  # a browser decodes both before resolving
        dest = os.path.normpath(part.lstrip("/") if part.startswith("/") else os.path.join(os.path.dirname(path), part)) if part else path
        if dest not in tracked or not (ROOT / dest).exists():  # tracked, and not deleted from the working tree
            failures.append(f"{path}:{number}: link to {target}: no such file in the repository")
        elif anchor and dest.endswith(".md") and dest in docs:
            if dest not in cached:
                cached[dest] = anchors(docs[dest])
            if anchor not in cached[dest]:
                failures.append(f"{path}:{number}: link to {target}: no heading of {dest} has the anchor #{anchor}")

    for path, doc in docs.items():
        defined = set()
        for number, line in enumerate(doc.lines, start=1):
            if definition := DEFINITION.fullmatch(line):
                defined.add(label(definition.group(1)))
                check(path, number, definition.group(2))
        for first, text in doc.prose():
            for link in LINK.finditer(text):
                check(path, first + text.count("\n", 0, link.start()), link.group(1))
            for use in REFERENCE.finditer(text):
                name = " ".join((use.group(2) or use.group(1)).split())
                if label(name) not in defined:
                    number = first + text.count("\n", 0, use.start())
                    failures.append(f"{path}:{number}: link to reference [{name}]: no definition `[{name}]: <target>` in the file")


def check_code_spans(docs: dict[str, Doc]) -> None:
    for path, doc in docs.items():
        for number, span in doc.spans():
            if len(span) > 20 and (span[0].isspace() or span[-1].isspace()) and span.strip():
                shown = span if len(span) <= 60 else span[:60] + "…"
                failures.append(f"{path}:{number}: code span `{shown}` starts or ends with a space: a backtick is paired with the wrong partner")


def check_tables(docs: dict[str, Doc]) -> None:
    for path, doc in docs.items():
        run: list[int] = []
        for number, line in enumerate(doc.lines + [""]):
            if TABLE_ROW.match(line):
                run.append(number)
                continue
            if run and (len(run) < 2 or not SEPARATOR.fullmatch(doc.lines[run[1]])):
                failures.append(f"{path}:{run[0] + 1}: a table row with no header and separator line above it: a row cut off from its table, or text inside one")
            run = []


def check_union_duplicates(files: list[str]) -> None:
    for record in git("check-attr", "--stdin", "merge", stdin="\n".join(files)).splitlines():
        path, _, value = record.rpartition(": merge: ")
        if value != "union" or not (ROOT / path).is_file():
            continue
        lines, targets = {}, {}
        for number, line in enumerate((ROOT / path).read_text(encoding="utf-8").splitlines(), start=1):
            if line.strip() and line.rstrip() in lines:
                failures.append(f"{path}:{number}: repeats line {lines[line.rstrip()]}; a union merge keeps both sides of a line both branches changed")
            lines.setdefault(line.rstrip(), number)
            for target in LINK.findall(line):
                if target in targets and targets[target] != number:
                    failures.append(f"{path}:{number}: links {target}, as line {targets[target]} does")
                targets.setdefault(target, number)


def python_tests(text: str) -> dict[str, set[str]]:
    """Each class of a Python source whose name ends in Tests, with the methods its body declares."""
    try:
        tree = ast.parse(text)
    except SyntaxError:
        return {}
    return {
        node.name: {member.name for member in node.body if isinstance(member, (ast.FunctionDef, ast.AsyncFunctionDef))}
        for node in ast.walk(tree) if isinstance(node, ast.ClassDef) and node.name.endswith("Tests")
    }


def csharp_tests(text: str) -> dict[str, set[str]]:
    """Each class of a C# source whose name ends in Tests, with the methods its body declares, under its own name and
    under that name qualified by the first namespace the file declares."""
    code, found = CS_NOT_CODE.sub(" ", text), {}
    namespace = re.search(r"\bnamespace\s+([\w.]+)", code)
    for declared in re.finditer(r"\bclass\s+(\w+Tests)\b", code):
        start = code.find("{", declared.end())
        members, depth = [], 0  # the body's own text; a member's braces, and all they hold, become the `;` that ends it
        for char in code[start:] if start != -1 else "":
            if char == "{":
                depth += 1
                members.append(";" if depth == 2 else "")
            elif char == "}":
                depth -= 1
                if depth == 0:
                    break
            elif depth == 1:
                members.append(char)
        body = "".join(members)
        while (bare := re.sub(r"\[[^\[\]]*\]", " ", body)) != body:  # attributes, and the brackets of an array type
            body = bare
        # A method's name comes before the member's first parenthesis, with no `=` on the way: that is an initializer.
        declarations = (re.match(r"[^=(]*?\b(\w+)\s*(?:<[^<>()]*>)?\s*\(", member) for member in body.split(";"))
        methods = {match.group(1) for match in declarations if match}
        for name in {declared.group(1), f"{namespace.group(1)}.{declared.group(1)}" if namespace else declared.group(1)}:
            found.setdefault(name, set()).update(methods)
    return found


def check_test_pointers(docs: dict[str, Doc], files: list[str]) -> None:
    if ACCEPTANCE not in docs:
        return
    classes: dict[str, set[str]] = {}  # each test class's name, with the methods every class of that name declares
    for path in files:
        if TEST_SOURCE.fullmatch(path) and (ROOT / path).is_file():
            text = (ROOT / path).read_text(encoding="utf-8", errors="ignore")
            for name, methods in (python_tests if path.endswith(".py") else csharp_tests)(text).items():
                classes.setdefault(name, set()).update(methods)
    namespaces = {name.rpartition(".")[0] for name in classes if "." in name}  # `PouryaKit.ArchitectureTests`: a project
    reported = set()
    for number, span in docs[ACCEPTANCE].spans():
        qualified = QUALIFIED_POINTER.fullmatch(span)
        pointer = qualified if qualified and qualified.group(2) is None and qualified.group(1) in classes else TEST_POINTER.fullmatch(span) or qualified
        if not pointer or span in reported or span in namespaces:
            continue
        name, method = pointer.groups()
        if name not in classes or method is not None and method not in classes[name]:
            reported.add(span)
            missing = f"the class {name} has no method {method}" if name in classes else f"no test class {name}"
            failures.append(f"{ACCEPTANCE}:{number}: `{span}` names no test: {missing}; a renamed test is found with `git log -S{method or name.rpartition('.')[2]}`, and a removed one leaves its row")


def check_kit_paths(docs: dict[str, Doc], files: list[str]) -> None:
    present = {f for f in files if (ROOT / f).exists()}  # tracked, and not deleted from the working tree
    present |= {str(parent) for f in present for parent in Path(f).parents[:-1]}
    cited = []
    for path, doc in docs.items():
        if path.startswith("docs/") and path.count("/") == 1:
            cited += [(path, number, match.group(1)) for number, span in doc.spans() if (match := CITED_PATH.fullmatch(span))
                      and match.group(1).startswith(KIT_ROOTS) and not GENERATED.match(match.group(1))]
    for path, number, kit_path in cited:
        pattern = kit_path.rstrip("/")
        if not (any(fnmatch.fnmatchcase(f, pattern) for f in present) if "*" in pattern else pattern in present):
            failures.append(f"{path}:{number}: `{kit_path}` names no file or folder in the repository; cite a moved file at its current path")


DIAGRAMS = "docs/architecture/diagrams/"
REVERIFY = "re-verify the cited ranges against the current code, refinalize the diagram, and set meta.repository.revision to the commit you verified against"


def cited_sources(node):
    """Each entry of every `sources` list in a diagram source, wherever its type puts one: on a component, a node, a
    participant or a state."""
    if isinstance(node, dict):
        for key, value in node.items():
            yield from (item for item in value if isinstance(item, dict)) if key == "sources" and isinstance(value, list) else cited_sources(value)
    elif isinstance(node, list):
        for item in node:
            yield from cited_sources(item)


STAGED = False


def check_diagram_sources(files: list[str]) -> int:
    """Fails each cited range of a diagram source that may no longer say what the code does; returns the diagrams read."""
    candidates = git("ls-files", "--cached", "--", DIAGRAMS).splitlines() if STAGED else files
    diagrams = [f for f in candidates if f.startswith(DIAGRAMS) and f.endswith(".json") and (STAGED or (ROOT / f).is_file())]
    for diagram in diagrams:
        if STAGED:
            read = subprocess.run(["git", "show", f":{diagram}"], cwd=ROOT, capture_output=True, text=True)
            if read.returncode:
                failures.append(f"{diagram}: cannot read diagram source from the index")
                continue
            text = read.stdout
        else:
            text = (ROOT / diagram).read_text(encoding="utf-8")
        data = json.loads(text or "{}")
        sources = list(cited_sources(data))
        revision = str(data.get("meta", {}).get("repository", {}).get("revision", ""))
        if not sources:
            continue
        # In HEAD's history, not only present: a commit a rebase or squash left behind stays in a local clone for a while.
        # While a merge is being committed, the incoming branch's commits are not HEAD's ancestors yet: MERGE_HEAD's count.
        heads = ["HEAD"] + (["MERGE_HEAD"] if not subprocess.run(["git", "rev-parse", "-q", "--verify", "MERGE_HEAD"], cwd=ROOT, capture_output=True).returncode else [])
        if not re.fullmatch(r"[0-9a-f]{40}", revision) or all(subprocess.run(["git", "merge-base", "--is-ancestor", revision, head], cwd=ROOT, capture_output=True).returncode for head in heads):
            failures.append(f"{diagram}: meta.repository.revision {revision or '(none)'} is unreachable here: a shallow clone, or a revision squashed away; {REVERIFY}")
            continue
        paths = sorted({str(source.get("path")) for source in sources})
        # One call each for every cited file: its line count in the selected snapshot (an absent path is not listed),
        # and whether it changed since the revision. Literal pathspecs, so a path is never read as a glob.
        snapshot = "the index" if STAGED else "HEAD"
        grep_args = ("--cached", "-c", "") if STAGED else ("-c", "", "HEAD")
        counts = subprocess.run(["git", "-c", "core.quotePath=false", "--literal-pathspecs", "grep", *grep_args, "--", *paths], cwd=ROOT, capture_output=True, text=True).stdout
        lines = {path: int(count) for path, _, count in ((row if STAGED else row.removeprefix("HEAD:")).rpartition(":") for row in counts.splitlines())}
        # The hunks that moved or rewrote lines of each cited file since the revision, as (first new line, new line count).
        # A change strictly below a cited range leaves the range's lines where they were; one at or above it does not.
        against = ["--cached", revision] if STAGED else [revision, "HEAD"]
        hunks = {
            path: [
                (int(hunk.group(1)), int(hunk.group(2)) if hunk.group(2) is not None else 1)
                for hunk in re.finditer(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", git("-c", "core.quotePath=false", "--literal-pathspecs", "diff", "-U0", *against, "--", path), re.M)
            ]
            for path in paths
        }
        if STAGED and git("diff", "--name-only", "--cached", "--", diagram).strip():
            # The commit being made updates the diagram with the cited file (its pin follows in the next commit), so only
            # a cited file staged without its diagram is judged.
            for path in git("diff", "--name-only", "--cached", "--", *paths).splitlines():
                hunks[path] = []
        for source in sources:
            path, first = str(source.get("path")), source.get("line")
            last = source.get("end_line", first)
            if path not in lines:
                problem = f"names no file at {snapshot}"
            elif not all(type(n) is int for n in (first, last)) or not 1 <= first <= last <= lines[path]:
                problem = f"is no ordered range within the file's {lines[path]} lines"
            elif any(start <= last if count else start < last for start, count in hunks.get(path, [])):
                problem = f"cites lines changed or moved since the revision {revision[:7]}"
            else:
                continue
            failures.append(f"{diagram}: {path}:{first}" + (f"-{last}" if last != first else "") + f" {problem}; {REVERIFY}")
    return len(diagrams)


def print_fences() -> int:
    changed = git("diff", "--name-only", "HEAD").splitlines() + git("ls-files", "--others", "--exclude-standard").splitlines()
    shown = 0
    for path in sorted(p for p in set(changed) if p.endswith(".md")):
        old = subprocess.run(["git", "show", f"HEAD:{path}"], cwd=ROOT, capture_output=True, text=True).stdout
        new = (ROOT / path).read_text(encoding="utf-8") if (ROOT / path).is_file() else ""
        before, after = blank_code(old)[1], blank_code(new)[1]
        kinds = sorted({info.split()[0] if info else "plain" for info, _ in after})
        print(f"{'same   ' if before == after else 'CHANGED'} {path}: {len(before)} -> {len(after)} fenced blocks {kinds}")
        shown += 1
    print(f"check-docs --fences: {shown} changed Markdown file(s) against HEAD")
    return 0


def main() -> int:
    if sys.argv[1:] == ["--fences"]:
        return print_fences()
    global STAGED
    STAGED = sys.argv[1:] == ["--staged"]
    if sys.argv[1:] and not STAGED:
        print(__doc__.strip(), file=sys.stderr)
        return 2
    files = listed()
    skipped = vendored()
    docs = {f: Doc(f) for f in files if f.endswith(".md") and not f.startswith(skipped) and (ROOT / f).is_file()}
    pages = {f: Doc(f) for f in files if SITE_PAGE.fullmatch(f) and (ROOT / f).is_file()}
    check_names(docs | pages, files)
    check_links(docs, files)
    check_code_spans(docs)
    check_tables(docs)
    check_union_duplicates(files)
    check_test_pointers(docs, files)
    check_kit_paths(docs, files)
    diagrams = check_diagram_sources(files)
    for message in failures:
        print(f"FAIL  {message}")
    print(f"check-docs: {len(failures)} failure(s) in {len(docs)} Markdown file(s) and {diagrams} diagram source(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
