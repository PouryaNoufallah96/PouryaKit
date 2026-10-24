# AI setup

How PouryaKit is worked on with Claude Code and OpenAI Codex: the roles, the ticket loop, the main session's output style and mod, the guard, first-time Codex setup, and what was deliberately left out. The operating model is in `AGENTS.md`.

## Layout

One home per concern. Anything both tools use lives in `.agents/` or at the root; `.claude/` and `.codex/` hold only what each tool loads natively. Nothing is generated.

| Thing | Home | Loaded by |
|---|---|---|
| Shared instructions | `AGENTS.md` | Codex directly; Claude Code through `CLAUDE.md`, which is the bare `@AGENTS.md` import |
| Main-session guidance | `.claude/output-styles/kit-orchestrator.md` | Claude Code, main session only |
| Skills | `.agents/skills/<name>/` | Codex directly; Claude Code through the `.claude/skills/<name>` symlinks |
| The Kit's own skill | `.agents/skills/pourya-kit/`, owned by this repository and so absent from `skills-lock.json` | Same as other skills; workflow step 7 updates it as each Capability lands |
| Roles | `.claude/agents/<name>.md` paired with `.codex/agents/<name>.toml` | Each tool reads its own file |
| Rules | `.claude/rules/*.md` | Claude Code by `paths`; Codex through the rule router in `AGENTS.md` |
| Review workflow | `.claude/workflows/kit-review.js`; its results in the gitignored `.claude/reviews/` | Claude Code (`/kit-review`) |
| Session handoff | `.claude/handoff/<session>/`, gitignored: the briefs, reports, evidence files and `HANDOFF.md` of a run | The main session (`docs/agents/running-a-spec.md`, "State and handoff"); the next session reads it first |
| Guard | `.agents/hooks/guard.py` | Both, wired from `.claude/settings.json` and `.codex/hooks.json` |
| Mod | `.claude/mods/kit-cockpit/`, linked from `.claude/skills/kit-cockpit` | Claude Code |
| Status line | `.claude/statusline.sh` | Claude Code, main session, through `statusLine` in `.claude/settings.json` |
| Claude Code settings | `.claude/settings.json` | Claude Code: subagent default model, agent teams on, the output style, the status line, subagent rows, subagent worktree base, allow a branch `git push` and ask before a push to `main`, the guard |
| Codex config | `.codex/config.toml` | Codex, once the repository is trusted: model, effort, review model, subagent defaults |
| Check scripts | `.agents/scripts/check-ai-setup.py`, which also runs `.agents/scripts/check-tree.py` on every tracked path and `.agents/scripts/check-skills-lock.mjs` on the vendored skills; `.agents/scripts/check-docs.py` for the Markdown and, in its diagram sources section, the source ranges the architecture diagrams cite; the unit tests beside them (`test_*.py`) | Run by hand after any change, and by the `ai-setup` CI job; `check-tree.py` also by the `pre-commit` hook; `check-docs.py` also by every implementer's `verify` |
| CI failure reader | `.agents/scripts/ci-failures.py` | Run by hand on a failed CI run (`docs/agents/running-a-spec.md`) |
| Merge helper | `.agents/scripts/merge-package.py`, with its tests beside it | Run by the main session on each implementer report (`docs/agents/running-a-spec.md`, "State and handoff"): merges the reported head, checks the tree and runs the check scripts, pushes only when they hold. Its `--ready` mode is the close-out check before the Wave's pull request is marked ready ("What changes from the skill" there) |
| Pull request threads | `.agents/scripts/pr-threads.py`, with its tests beside it | Run by whoever owns a pull request's review round (step 4 below) |
| Local CI reproduction | `.agents/scripts/ci-local.sh` | Run by hand while hosted runs are unavailable (`docs/ci.md`): every `run:` step of CI's `ai-setup` and package validation jobs but the two that read the Actions run, on the checked-out commit, each step's output kept; a local reproduction, never a CI result |
| Report saver | `.agents/scripts/save-report.py` | Run by the main session as each role's final report arrives (`docs/agents/running-a-spec.md`, "State and handoff"): writes the agent's final message from its transcript to the report file `merge-package.py` reads |
| Role memory | `.claude/agent-memory/kit-implementer/` | Claude Code, for `kit-implementer` only; created on first use, committed |
| Git hooks | `.githooks/` | Git, once a clone runs `git config core.hooksPath .githooks`: `pre-commit` and `pre-merge-commit` refuse staged merge conflict markers, and `pre-commit` an added, modified or renamed staged path outside the tree's top-level entries ("The guard") and a docs or AI-setup path staged in part; deleting a rejected path is allowed; `pre-commit` runs `check-docs.py --staged` on every commit (diagram sources and cited files come from the index: a change at or above a cited range requires its diagram staged too, with ranges that fit the indexed file; a change below every cited range passes, and a revision reachable from `MERGE_HEAD` counts during a merge) and `check-ai-setup.py` when an AI-setup path is staged; the other rules read the working tree, so a source file staged in part can make names or test declarations differ from the commit, and CI's `ai-setup` job on the committed tree is the gate |
| Process docs | `docs/agents/running-a-spec.md`, `docs/agents/spikes.md`, `docs/agents/brief-template.md` | The main session, through the output style and step 6 below. `spikes.md` reaches a Spike's implementer and its record's reviewer through their briefs, as `running-a-spec.md` says; the "Spikes" line of `.claude/rules/kit-code.md` also names it for work under `src/`, `tests/` and `samples/` |

## Roles

Each role is one pair with the same `name`, `description` and instructions. The contract is in the role file. Each role pins its own model and effort.

| Role | Access |
|---|---|
| `kit-scout` | read-only |
| `kit-implementer` | writes inside `allowed_files`; in Claude Code it also keeps project memory, the one exception to `allowed_files` |
| `kit-reviewer` | read-only, no memory |

`kit-reviewer` has no memory on purpose: an independent reviewer carries no conclusions from one diff into the next.

Routing:

| Role | Claude Code | Codex |
|---|---|---|
| `kit-implementer` | `claude-opus-5-5`, effort `xhigh` | `gpt-6.1-sol`, effort `xhigh` |
| `kit-reviewer` | `claude-fable-5-1`, effort `high` | `gpt-6-astra`, effort `high` |
| `kit-scout` | `claude-opus-5-5`, effort `medium` | `gpt-6.1-sol`, effort `medium` |

Claude reads `model` and `effort` from the role's frontmatter. Codex reads `model` and `model_reasoning_effort` from the role's `.toml`. A Claude spawn that passes a model uses that model instead. A Codex role file's model and effort take precedence over a spawn override. `CLAUDE_CODE_SUBAGENT_MODEL` (`opus`) and the `[agents]` defaults cover a subagent that is not one of these roles. `/kit-review`'s Claude review leg spawns `kit-reviewer`. Its Codex leg is `codex exec` with no `--model` unless `codexModel` is passed, so it uses `model` and `model_reasoning_effort` in `.codex/config.toml`, which are `gpt-6-astra` and `high`. The prepare, verify and finish agents in that workflow stay on the host default.

## The ticket loop

The steps and gates are in `AGENTS.md`, "Workflow for every ticket". This is how each one runs.

**Before step 1: scope and brief.** Read the ticket (`gh issue view <n> --comments`) and record the base commit (`git rev-parse HEAD`). Apply the scope and early external-probe rules in `AGENTS.md` before full implementation. `/kit-review` fetches the ticket itself, so no spec file is needed for it.

**Steps 1–2: implement, then focused tests.** Choose the task shape from `AGENTS.md`, "Operating model". A clear, small delegated fix uses one bounded worker; a normal ticket uses `kit-implementer` with the brief. Check its report against the tree: read the diff and the `verify` output it reports. Re-run a check only when that leaves a doubt; the current models verify their own work, and a second run of everything is wasted time.

**Step 3: review, sized to the ticket.** Use the review size from `AGENTS.md`. When one reviewer is required, give `kit-reviewer` the base and the spec. For dual review, the saved workflow in Claude Code, `.claude/workflows/kit-review.js`, is:

```text
Run /kit-review with base <base commit> ticket 42 head <the implementer's head commit>
```

The workflow takes its arguments as a JSON object or as the word form above (`key value` pairs: `base`, `ticket`, `spec`, `head`, `previous`, `rescore`, `evidence`, `codexModel`, `codexEffort`; `scope` is JSON-only). `base` is the `base` the implementer's report names. `evidence` is the path of a file the session writes with the recorded execution evidence (the verify commands and their totals, the package validation PASS line, the red-once list): the prepare stage appends it to `spec.md`, together with the body and the comments of the parent spec named by the ticket's "Parent" link (the owner's guidance and decisions live in the comments), so the Codex leg has the spec it cannot fetch and the record it cannot run. A record that names the revision, commands, outcomes and environment counts as the execution record; BLOCKED remains the right verdict when required evidence cannot be inspected at all.

For an explicit Codex model instruction, pass it through, for example `codexModel gpt-6.1-sol`. The Claude review leg uses `kit-reviewer`'s model and effort. The prepare, verify and finish agents use host defaults. If the session restricts all agents to one provider, run two independent reviewer agents in that host with the required model instead of invoking this cross-provider workflow. Apply the same review criteria and limits from `AGENTS.md`.

The work to review must be committed: the review reads one commit, never uncommitted changes. Pass `spec` (a file path, absolute or relative to the repository root) instead of `ticket` only for work with no ticket; exactly one of the two. Optional arguments: `head` (the commit to review; by default the commit your checkout is on when the run starts, which in this repository is the integration branch's tip, so pass the implementer's head: a `head` that resolves to `base` is refused with no diff to review; session 5 lost one run that way), `scope` (one line of plain text), `codexModel` (an explicit Codex model; omitted uses its host configuration), `codexEffort` (explicit `high` or `xhigh`; omitted uses host configuration), and `previous` with `rescore` for a re-review (step 4). A wrong combination fails at once and says what to pass.

The workflow resolves `base` and `head` to hashes, saves the ticket's title and body as `spec.md` in a run directory under the system temp directory (with the parent spec's body and comments, and the `evidence` file, appended when present), and checks `head` out as a detached worktree under `.claude/worktrees/kit-review-<random>`. The Codex inspection reviews that worktree first, alone, then `kit-reviewer`, which may build and run tests there (the two ran in parallel until 2026-10-07, when the builds starved the Codex runner's 570 s budget twice); one verification stage then merges duplicate findings and checks each against the code. When the run ends, on success or failure, the worktree is removed with `git worktree remove`. Your checkout is never read for the diff, so you can keep working, merging and running implementers during a review. A changed submodule or nested repository in the diff still aborts the Codex leg.

The value returned is a digest that a task notification shows in full: it stays under about 5.5 KB whatever the number of findings. It starts with `complete`, `verdict`, `result`, `run` and, when needed, `cleanup` and `error`, then `counts` and each leg's verdict and one-line `note`. A re-review adds `rescored`, a list with each earlier finding's `id`, `state` and the `evidence` for it. Then come the findings, standing ones first and most severe first, each with only `id`, `severity`, `status`, `path`, `by` (who raised it) and a `claim` of at most 160 characters. When they do not all fit, the claims shrink to 100 and then 60 characters, and after that the list stops where the budget ends, with `omitted` giving how many more are in the file. `result` names the file with the full result, `.claude/reviews/<ticket number or spec file name>-<short head hash>.json` under the repository root, which outlives the system temp directory; it holds every finding's evidence, fix and verification, and each leg's read count and limitations. Read that file when you need more than the digest. `run` names the run directory, which keeps `spec.md` and the same `result.json`. If the file could not be written, `result` is null and the full result comes back under `full`, at the end; if the worktree could not be removed, `cleanup` gives the command.

Each finding has a stable `id` (`F1`, `F2`, …) and a status of `confirmed`, `refuted` or `unverified`. `pass`, `fix` and `block` are computed from the findings that were not refuted: `block` when one of them is high, `fix` when any stands, `pass` when none does.

`unknown` is never a pass. It means a leg or the verification stage did not return a usable review. A leg counts only when it reports that it ran, gives a verdict that means "reviewed" (`block`, `fix` or `pass` from `kit-reviewer`; `APPROVED` or `REVISE` from Codex), read at least one changed file and left none of the diff unread, and its verdict fits its findings. The leg's `note` says which of these failed. When the Codex runner fails, the note quotes the runner's own reason, for example "Selected model is at capacity". The workflow does not retry it.

The main session judges the findings; the workflow edits nothing in the reviewed commit.

The Codex leg has about 9.5 minutes, because a foreground shell call is capped at 10. When it runs out, the verdict is `unknown`. Run that leg yourself in the background against a fresh worktree of the same commit, using the `spec.md` from the run directory named in `run`, and judge its findings together with the `kit-reviewer` leg:

```bash
git worktree add --detach .claude/worktrees/kit-review-codex <head>
python3 .agents/skills/claudex-loop/scripts/runner.py inspect --host claude --builder claude \
  --repo .claude/worktrees/kit-review-codex --plan <run directory>/spec.md --base <base> \
  --timeout 1800
git worktree remove .claude/worktrees/kit-review-codex
```

The runner's first output line names an artifacts directory; `result.json` there holds the verdict, findings, coverage and limitations. Omitted `--model` and `--effort` use the provider's host configuration. Add them when the session specifies overrides. Copy that `result.json` to `.claude/reviews/<ticket or spec name>-<short head hash>.codex.json` beside the stable result file and name both in the ticket's evidence: the stable file keeps `complete: false` and the digest's `unknown`, which say the workflow's own leg did not return, not that the review failed, and a later reader needs the rerun's verdict beside it.

Over 900,000 bytes of diff, the workflow skips the Codex leg before the runner starts, and the leg's note gives the size. `inspect` puts the whole diff in Codex's prompt, and Codex refuses an input over 1,048,576 characters: the final structural review of 2026-10-08 failed in two seconds on a 2.68 MB diff with "Input exceeds the maximum length of 1048576 characters". Run that leg by hand in review mode, with no `--base`. Codex then reads the worktree itself instead of an embedded diff, so the review brief names the base and head commits beside the spec, and `--artifacts` is a directory outside the checkout:

```bash
python3 .agents/skills/claudex-loop/scripts/runner.py review --host claude --builder claude \
  --repo <detached worktree> --plan <review brief> --artifacts <dir> --timeout 1800
```

Its `result.json` is kept as above, under `.claude/reviews/` beside the stable result file.

For the Claude leg over a diff of that size, one `kit-reviewer` cannot read every file, and a leg counts only with none of the diff unread: run several `kit-reviewer`s in parallel on disjoint path slices (Wave 3's audit used four: the write-model core, the capability packages, the sample with its tests and templates, docs with scripts and the AI setup), each on its own detached worktree of the same commit, with one common brief and one slice brief naming its paths, and judge the union. Name the per-shard read counts in the record, never a total that hides a shard's partial coverage.

From a Codex-hosted session, spawn the `kit-reviewer` agent for the Codex review and run the Claude inspection through the same runner, on a worktree of the commit as above, with the ticket saved as a spec file outside the checkout:

```bash
gh issue view 42 --json title,body --jq '"# " + .title + "\n\n" + .body' > /tmp/kit-ticket-42.md
python3 .agents/skills/claudex-loop/scripts/runner.py inspect --host codex --builder codex \
  --repo .claude/worktrees/kit-review-codex --plan /tmp/kit-ticket-42.md --base <base>
```

**Step 4: fixes and re-scores.** Follow the review limits and small-change exception in `AGENTS.md`; normal tickets use `kit-implementer` for fixes and `kit-reviewer` for re-scores from fresh evidence. Before fixing, fetch all currently available review threads, deduplicate and judge their findings, then give one combined brief and push the fixes together; handle material new findings when they arrive. One owner monitors required checks through completion; the main session judges that report instead of polling in parallel. On a pull request, `.agents/scripts/pr-threads.py` does the three: `list <pr>` prints the unresolved threads with the reviewers' boilerplate stripped, `reply-resolve <pr> <replies.json>` answers and resolves each one, and `wait <pr>` returns when the checks and the Codex review have finished; its docstring gives the file's form and the exit codes.

Only after a high finding was fixed and committed, and only once per ticket, run `/kit-review` again with the same `base` and `ticket`, `head` set to the implementer's fixed commit, and `previous` set to the earlier result file: the path in its `result`, or `result.json` in its `run` directory. When the earlier digest's `result` is null, save the `full` object from that run's task output to a file yourself and pass that path as `previous`:

```text
Run /kit-review with base <base commit>, ticket 42, head <the implementer's fixed commit>, previous <earlier result file> and rescore F1,F3
```

`rescore` is optional: leave it out to re-score every earlier finding that was not refuted, or list the ids you accepted. `kit-reviewer` re-scores each of them, and Codex inspects the whole change fresh. If the earlier findings do not all reach the reviewer, the run stops with `unknown` before reviewing.

The digest's `rescored` list gives each of them one state: `resolved`, `partial` or `unresolved`. A finding the reviewer did not re-score counts as `unresolved`. Each `partial` or `unresolved` one still stands under its earlier `id`, at no less than its earlier severity, and the full result shows its re-score under `previous`; a finding new in this round gets the next `F` number after the highest one any earlier round used, so an id is never reused.
**Step 5: complete runtime proof.** The main session checks the real-service probe evidence and runs any remaining proof commands required by the ticket.

**Steps 6–7: docs and AI guidance.** For a doc a person reads (`README.md`, `docs/architecture/`, an ADR), the owner can type `/technical-writing` with the file to write or review; it applies `unslop` as well, and `writing-for-agents` stays the reference for the docs agents read. The value, cost and impact paragraph goes in the ticket's closing comment, whose other contents `docs/agents/running-a-spec.md` lists; `/archify-review` runs at the wave's close-out. After updating the AI guidance, run the check script (below).

## The main session in Claude Code

**Output style.** `.claude/output-styles/kit-orchestrator.md`, selected by `outputStyle` in `.claude/settings.json`, puts the main session in the judge-and-orchestrator role and says how to choose between a role subagent, an agent team and `/kit-review`. Claude Code applies an output style to the main conversation and its forks only; role subagents never see it, which is why `CLAUDE.md` stays a bare import. Switch styles for yourself with `/output-style`.

**Agent teams.** `.claude/settings.json` enables them with `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`. Set the variable to `0` in `.claude/settings.local.json` (your own, untracked file) to turn them off for yourself. A session has one team, and teammates are not restored on resume.

**Status line.** `statusLine` runs `.claude/statusline.sh`, which prints one line for the main session, such as `Opus 5.5 · xhigh · ctx 42% · 5h 31% · main · kit-orchestrator`: model, effort, context use, the five-hour limit, branch, and the output style when it is not the default. A part the payload does not carry is left out, and `ctx` turns red from 80%. A project `statusLine` replaces the user-level one, so the script first passes the same payload to the `statusLine` command in your own user settings and prints whatever that prints above its own line; an integration that lives in the user's own status line command keeps working. It needs `jq`: without it the line is blank and the user's own status line command is not run.

**Subagent worktrees.** Claude Code creates each subagent worktree itself under the gitignored `.claude/worktrees/`, on a new branch, and removes it the usual way: at once if the agent changed nothing, otherwise by its periodic sweep. `"worktree": {"baseRef": "head"}` in `.claude/settings.json` bases it on the commit the session's checkout is on at spawn, instead of the remote's default branch; uncommitted changes do not carry over. The check script fails without it. To turn it off for yourself, put `{"worktree": {"baseRef": "fresh"}}` in `.claude/settings.local.json`. Codex creates no per-subagent worktree.

**Subagent rows.** `subagentStatusLine` is an inline `jq` command that renders each row of the agent panel as `kit-implementer · opus-5-5 · xhigh · 24% ctx`, leaving out a field the task does not report. Without `jq` the default rows show.

**The `kit-cockpit` mod.** A mod is a plugin whose handlers run inside Claude Code. This one does two things:

- It draws one dim line above the prompt, such as `agents: kit-implementer · guard: 2 blocked, 1 overridden`. Each part appears only when there is something to show; the blocked count includes denies inside subagents. Figures the status line carries are not repeated here.
- It puts a guard deny to you as a question (see "The guard").

It loads as `kit-cockpit@skills-dir` through the `.claude/skills/kit-cockpit` symlink, once you have accepted the workspace trust dialog and only when the session starts in the repository root. To turn it off for yourself, set `"kit-cockpit@skills-dir": false` under `enabledPlugins` in `.claude/settings.local.json`. After editing it, run:

```bash
claude plugin validate .claude/mods/kit-cockpit
claude plugin test .claude/mods/kit-cockpit
```

A 30-second live check: start `claude` in the repository root and ask it to run `rm -rf ./no-such-directory`. The question "Keep blocked / Run it once" should appear, and after you keep it blocked the band should read `guard: 1 blocked`.

**Long runs.** Two vendored skills serve a session that runs for hours. The owner types each one; the model cannot invoke them.

- `/chief-of-staff <goal>` makes the session own a goal across many tasks. Beside the orchestration the output style already gives, it adds two duties: propose recurring schedules, and keep improving the environment the agents work in. The output style, "Long runs", sizes the second one for this repository.
- `/correct` takes the mistakes the owner has corrected at least twice and makes each one impossible: architecture first, then types or a check whose message names the fix, then a test, and a written rule last. Each new check is proven to fail on the real past mistake. It is the step after `/retro`.

`chief-of-staff` comes from the `in-progress/` bucket of `mattpocock/skills`, which upstream publishes as beta and may still change.

## The guard

`.agents/hooks/guard.py` runs before tool calls in both tools, including inside subagents. The script only denies, with a reason, or allows. In Claude Code the `kit-cockpit` mod turns a denied shell command of the main session into the question "Keep blocked / Run it once"; "Run it once" allows that one call. File-tool denies and calls inside subagents stay denied, and so does everything in Codex and in `claude -p`.

To turn the guard off for yourself, add `"env": { "POURYAKIT_GUARD": "off" }` to `.claude/settings.local.json`: the guard then allows every call, and the mod and status line keep running. No settings file can switch off one project hook, and `disableAllHooks` would stop the status line and the mod's band too.

The guard is a seatbelt against accidents, not a sandbox. It stops the common destructive slip; it does not contain a session that sets out to get around it.

| Rule | Denied | Allowed |
|---|---|---|
| Writes stay in the working tree | A file-tool write (Claude Code `Edit`, `Write`, `NotebookEdit`; Codex `apply_patch`) whose target is outside the repository | The repository, system temp directories, `~/.claude/`, `~/.codex/` |
| Secret files | A file-tool read or write of `.env`, `.env.*`, `*.pem`, `*.key`, judged on the requested name and on the symlink target. Claude Code: `Read`, `Grep` whose `path` or `glob` names one, `Edit`, `Write`, `NotebookEdit`. Codex: `apply_patch` | Templates such as `.env.example` |
| Recursive delete | `rm -r`, `find -delete`, `find -exec rm`, `git clean -d` | One form only: a direct `rm -r` (optionally under `sudo`, `env` and the like) whose every argument is an option or a literal absolute path under a system temp directory, with no `--`. Never through `xargs` |
| Destructive push | `git push` with `--force`, `-f`, `--mirror`, `--delete`, `-d`, `+refspec` or `:branch` | `--force-with-lease` |

The first rule is how "never change the reference projects" is enforced without naming their locations. Beside the guard, the two Git hooks in `.githooks/` (`pre-commit`, `pre-merge-commit`, both running `staged-conflict-markers`) refuse a commit whose staged content adds `<<<<<<<` or `>>>>>>>`; they read the index, so a marker fixed but not re-staged still blocks. `pre-commit` then runs `.agents/scripts/check-tree.py` on added, modified and renamed staged paths: it refuses a path whose first segment is not one of the tree's top-level entries, listed in the script, and a `.gitignore` line that ignores a tracked path. Deleted paths are excluded, so a commit can remove a rejected path to repair the tree. Enable them once per clone with `git config core.hooksPath .githooks`; the check script and CI still cover every tracked file. `.claude/settings.json` allows `Bash(git push *)` and asks when the command names `main` as a destination (`git push origin main`, `git push main`, `HEAD:main`, `refs/heads/main`) or uses `--all`. Ask is checked before allow, so those commands still prompt. A bare `git push`, or `git push -u origin HEAD`, is allowed: the rule sees the command text, not the branch checked out in this worktree. `git -C <dir> push` is a different command and is not covered by either rule. Codex has no ask rules; its approval policy stays in the user's own config, and this guard still blocks a destructive push.

The docstring of `guard.py` states how a shell command is matched (for example, after a wrapper such as `sudo` every guarded word is judged), what is not covered and what is over-blocked. What it does not say:

- `rm -rf /tmp/kit-test/out` is the shape of the one recursive delete that passes.
- On Codex, reads go through the shell, so there the secret rule covers patches only. `Glob`, and a `Grep` brace glob such as `*.{pem,key}`, are not judged.
- When harmless text is over-blocked, write it with the file tools instead of a shell command, or answer "Run it once".
- Every deny reason from the guard starts with the marker `PouryaKit guard: `, and the mod acts only on a deny whose reason carries it. A deny from any other hook, such as a user-level one, passes untouched. The check script fails when `guard.py` and the mod spell the marker differently.
- On Codex the guard runs only after the repository and the hook are trusted (next section).

## First-time Codex setup

Codex skips `.codex/config.toml`, project hooks and project rules until the repository is trusted. `AGENTS.md` and `.agents/skills` load either way.

1. Trust the repository: start `codex` in the repository and accept the trust prompt, or add a `[projects."<absolute repository path>"]` table with `trust_level = "trusted"` to your user-level Codex config. Trust is not inherited from a parent directory.
2. Trust the hook: run `/hooks` in Codex and trust the PouryaKit guard. Hooks are trusted by hash, so a changed hook entry is skipped until it is trusted again.
3. Confirm: `codex debug prompt-input "x"` prints the model-visible prompt, including the `AGENTS.md` chain, without a model call. `/debug-config` shows the active config layers.

Done when `/debug-config` lists the project layer and `/hooks` shows the guard as trusted.

## Changing the setup

`.claude/rules/ai-setup.md` holds the rules for editing these files. Finish every change with:

```bash
python3 .agents/scripts/check-ai-setup.py
python3 .agents/scripts/check-docs.py
```

Each needs Python 3.11 or later, and `check-ai-setup.py` also needs Node on the path. Each prints one `FAIL` line per problem naming the file and the reason, and exits non-zero on any. A rule glob that matches no file yet is a `WARN`. Besides the AI setup, `check-ai-setup.py` fails on a merge conflict marker in any tracked text file, on any `check-tree.py` offence among the tracked paths, on a vendored skill whose directory no longer matches its hash in `skills-lock.json` (`check-skills-lock.mjs`, which it runs with Node), on a skill directory that has no entry there and is not the Kit's own, and on a package count spelled out in `README.md`, the Kit's skill or `docs/architecture/README.md` (a number before "packages" or "nupkgs"), which would drift when a package is added: those name the packages of `scripts/packaging/inventory.json` instead. `check-docs.py` fails on a backticked name in the Kit's skill, `README.md`, `docs/architecture/` or a top-level doc that names nothing in the code (an illustrative or third-party name goes in its `ALLOWED` list, with the reason), a broken relative link or anchor, a code span paired with the wrong backtick, a table row cut off from its table, a line a union merge kept twice, a `Class.Method` pointer in `docs/acceptance.md`, bare or qualified by its namespace, to a test that no longer exists under that name, a source file whose comment says its code is adapted from another codebase while no record in `docs/provenance/` names it, and a Kit path that no longer exists, cited on a provenance record's **Kit files** line, or into `src/PouryaKit.*`, `samples/` or `tests/` elsewhere in a record or in a top-level doc (an upstream repository's path, a project generated from the templates and `docs/adr/` stay out), and an architecture diagram's source whose pinned revision is not in `HEAD`'s history, or one of its cited ranges whose file is gone at `HEAD`, whose lines run past the file's end or out of order, or whose file changed between the revision and `HEAD` (`docs/architecture/README.md`, "Diagrams", says what to do then); its docstring gives each rule and what it leaves out. Its `--fences` mode only prints whether each Markdown file changed against `HEAD` changed a fenced block, so an implementer knows whether the skill's snippet harness must run. The `ai-setup` job runs both, then the unit tests beside them, on the [CI workflow's documented triggers](../ci.md).

**Re-vendoring a skill.** `mattpocock/skills` is the main source: keep the skills vendored from it level with upstream. A skill that is new upstream is adopted only after it is judged on three points (the owner's rule, 2026-10-09): it is useful to the work done here, it is compatible with this repository's rules and layout, and it duplicates nothing already present. One that is left out is recorded under "Deliberately excluded" with its reason. The sync runs when the owner asks; nothing schedules it. Replace the skill's whole directory under `.agents/skills/` with the upstream directory its `skills-lock.json` entry names (`source` and `skillPath`), then set that entry's `computedHash` with `node .agents/scripts/check-skills-lock.mjs --write`. It is the hash the `skills` CLI writes: SHA-256 over each file's path relative to the skill directory followed by its bytes, with the files in JavaScript `localeCompare` order of those paths, so `agents/openai.yaml` comes before `SKILL.md`; that order is why the check runs on Node. A skill that is new here also needs its lock entry (`source`, `sourceType`, `skillPath`, and `ref` where the source's other entries carry one) and its `.claude/skills/` symlink. `docs/agents/issue-tracker.md` and `docs/agents/triage-labels.md` were seeded from the templates in `setup-matt-pocock-skills`: when a template changes upstream, carry the change into the doc and leave "Spec tickets" as it is, since that section is this repository's own.

## Verified against

Claude Code 2.1.289 and Codex CLI 0.160.0, on 2026-10-04 (`worktree.baseRef` on 2026-10-06), each key on its live page:

- Claude Code: `https://code.claude.com/docs/en/` + `sub-agents`, `memory`, `hooks`, `settings`, `settings-reference`, `worktrees`, `agent-teams`, `workflows`, `permission-modes`, `output-styles`, `statusline`, `plugins/loading`, `plugins/mods/overview`, `plugins/mods/events`, `plugins/mods/reference`, `plugins/mods/test`. Appending `.md` returns the raw page.
- Codex: `https://learn.chatgpt.com/docs/` + `agent-configuration/agents-md`, `agent-configuration/subagents`, `config-file/config-reference`, `hooks`, `build-skills`, `non-interactive-mode`, and `config-schema.json`.

## Deliberately excluded

Ideas were taken from the owner's Ultrabusiness and ChainReporter setups. These parts were left behind:

| Excluded | Reason |
|---|---|
| Manifest and sync generator | The generated copies had drifted from their sources when inspected. Symlinks and the check script cover the same need with nothing to regenerate. |
| Cursor layer | Not used on this repository. |
| GoalBuddy board | The bounded-writer, scout and skeptical-reviewer contracts are adopted; the board protocol and its runtime are not needed for a loop the main session judges. |
| `.learnings/` | Claude Code auto memory and role memory already hold this; a third store would be a second implementation. |
| Haiku prompt hooks | Soft checks that default to approve, on a model this repository does not delegate to. |
| Notification hooks | Personal runtime preferences; they belong in user settings. |
| Session figures, a pane, a command classifier in the mod | The status line and the agent panel carry the figures, and `guard.py` stays the only rule source. |
| A main-thread `--agent` orchestrator | It would replace the prompt and tool list of every session in the repository. `AGENTS.md` and the output style carry the operating model instead. |
| .NET layer rules | Only the scaffold exists. `.claude/rules/kit-code.md` states only decided facts. |
| A glossary structure test | Probed on 2026-10-06: 46 hits on 366 public type and member names in the 13 Kit assemblies, none of them real drift (`*ServiceCollectionExtensions`, `DomainEvent`, `Error.Message` and the like), while the words reviewers actually flagged ("request", "caller") had no hits. Glossary drift stays a reviewer rule. |
| Edits to vendored skills | `skills-lock.json` pins each one by hash. |
| Nine skills of `mattpocock/skills` | `setup-ts-deep-modules`, `migrate-to-shoehorn`, `scaffold-exercises` and `setup-pre-commit` are for TypeScript or Node projects. `git-guardrails-claude-code` would be a second guard. `loop-me` writes `workflows/` and `NOTES.md` at the workspace root, which the tree check refuses here; it suits a personal workspace. `writing-fragments`, `writing-beats` and `writing-shape` write articles, which is not engineering work on the Kit. With `claude-handoff` in the next row, these are the ten of the 38 skills upstream published at `b0618bc` that are not vendored. |
| `claude-handoff` of `mattpocock/skills` | It starts the next session with `claude --bg`. Probed here on 2026-10-09 with Claude Code 2.1.295: the session began in the checkout, entered a worktree of its own before its first write, could no longer see the gitignored `.claude/handoff/`, and then waited unseen on a permission prompt. Such a session cannot own the integration branch's checkout or keep `HANDOFF.md`. Setting `worktree.bgIsolation` to `"none"` would keep it in the checkout, and every other background session started in the repository with it. |
| The rest of pstack, and all of `addyosmani/agent-skills` | Evaluated on 2026-10-09 against the saved session transcripts. pstack's other skills are bound to Cursor's Task tool and its model rule file, or restate a rule `AGENTS.md` already has; none of its four skills vendored earlier had been invoked once. The osmani pack is a second spec-to-ship flow beside the one in use, and its skills trigger on their own. Its `doubt-driven-development` was read in full: it makes a cross-model question to the owner mandatory in every cycle, against the go-ahead rule of `AGENTS.md`, "Operating model"; it leans on that pack's personas and test-first skill, which are not here; and its fresh-context adversarial review is what `/kit-review`, `claudex-loop` and `interrogate` already do, at the cost of more review rounds and reviewer quota, the two largest time sinks those transcripts show. |
| Changes to user-level config | Three edits the owner approved, and nothing else: a key Claude Code no longer reads (`teammateDefaultModel`) and a key Codex ignores (`agents.max_depth`) were removed, and this repository was trusted in Codex. |

## Known gaps

- `kit-review` has run end to end in live reviews since 2026-10-06, starting with ticket #4, including re-reviews with `previous` and `rescore`. Not yet taken in a live run: a result too large for the digest, and the finish step copying a large result byte for byte; those paths were exercised only with stubbed agents.
- A script cannot run commands, so `kit-review` uses two small agents: one to resolve the commits, fetch the ticket and create the worktree, and one to write the full result file and remove the worktree. The second writes the result through a quoted heredoc, copies it to `.claude/reviews/` and parses the copy back in the same command; the workflow counts the file saved only when the verdict read back is the one written, runs that agent once more when it is not, and otherwise returns the full result itself. The byte count it also reports is information only. A run that is stopped by hand skips the cleanup: remove its worktree with `git worktree remove --force .claude/worktrees/kit-review-<random>`.
- `/kit-review` supplies the cross-provider dual review when workflow step 3 requires it; a single reviewer or session-directed same-provider pair follows `AGENTS.md`. The vendored `code-review` skill, which reviews along a Standards axis and a Spec axis, is not part of the ticket gate.
- The guard was exercised with Codex-shaped payloads through the check script, not inside a live Codex session. Whether Codex loads `.codex/agents/*.toml` could not be observed without a model call.
- Mods are early access (Claude Code 2.1.287 or later), and their API may change between releases; re-run the two `claude plugin` commands after an upgrade. `kit-cockpit` passes them and loads, but it has not run in a live session. Unconfirmed: that the guard's deny reaches the mod as a `PreToolUse` deny with the marker still in its reason (without it the mod asks nothing and the deny stands), that the question appears with permission prompts off and "Run it once" then runs the command, what the band shows for role subagents and teammates, and hot reload. The 30-second check settles the first two.
- The status line script was tested against mock payloads and a stub user command, not in a live session and not against a live feed to the user's own status line command. Whether `statusLine` resolves the script when a session starts in a subdirectory depends on Claude Code loading the project settings there; the command falls back to the repository root through `git`.
- The subagent row command was tested against mock payloads, not a live agent panel.
- `worktree.baseRef: head` is confirmed: a subagent worktree created on 2026-10-06 still started at the remote's default branch, but every spawn of sessions 4 and 5 (2026-10-07 and 2026-10-08) started at the checkout's commit, such as #54's docs package at `325834f`, `feat/wave-2`'s tip, while `origin/main` was at `94f2dd6`. The role file's base check stays the backstop.
- The sizing of `/chief-of-staff` is in the output style, which only Claude Code's main session reads. In Codex the skill runs as upstream wrote it, bounded by `AGENTS.md`, "Operating model", alone.
- Six vendored skills (`blast-radius`, `correct`, `how`, `interrogate`, `technical-writing`, `unslop`) set `disable-model-invocation` and have no `agents/openai.yaml`, so Codex can invoke them implicitly. Fixing that means editing vendored skills.
- The vendored `implement` skill says to call the `tdd` skill where possible, and `tdd` triggers on "wants integration tests". The workflow in `AGENTS.md` overrides both: implement first, then focused tests. The vendored skills stay as vendored, with no content edit (decisions of 2026-10-06 and 2026-10-09).
