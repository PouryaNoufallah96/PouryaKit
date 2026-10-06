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
| Review workflow | `.claude/workflows/kit-review.js` | Claude Code (`/kit-review`) |
| Guard | `.agents/hooks/guard.py` | Both, wired from `.claude/settings.json` and `.codex/hooks.json` |
| Mod | `.claude/mods/kit-cockpit/`, linked from `.claude/skills/kit-cockpit` | Claude Code |
| Status line | `.claude/statusline.sh` | Claude Code, main session, through `statusLine` in `.claude/settings.json` |
| Claude Code settings | `.claude/settings.json` | Claude Code: subagent default model, agent teams on, the output style, the status line, subagent rows, subagent worktree base, ask before `git push`, the guard |
| Codex config | `.codex/config.toml` | Codex, once the repository is trusted: model, effort, review model, subagent defaults |
| Check script | `.agents/scripts/check-ai-setup.py` | Run by hand after any change, and by the `ai-setup` CI job |
| Role memory | `.claude/agent-memory/kit-implementer/` | Claude Code, for `kit-implementer` only; created on first use, committed |
| Process docs | `docs/agents/running-a-spec.md`, `docs/agents/spikes.md` | The main session, through the output style and step 6 below. `spikes.md` reaches a Spike's implementer and its record's reviewer through their briefs, as `running-a-spec.md` says; the "Spikes" line of `.claude/rules/kit-code.md` also names it for work under `src/`, `tests/` and `samples/` |

## Roles

Each role is one pair with the same `name`, `description` and instructions. The contract is in the role file; models stay unpinned.

| Role | Access |
|---|---|
| `kit-scout` | read-only |
| `kit-implementer` | writes inside `allowed_files`; in Claude Code it also keeps project memory, the one exception to `allowed_files` |
| `kit-reviewer` | read-only, no memory |

`kit-reviewer` has no memory on purpose: an independent reviewer carries no conclusions from one diff into the next.

Routing follows the session instruction in `AGENTS.md`. With no explicit override, Claude Code roles use `CLAUDE_CODE_SUBAGENT_MODEL` in `.claude/settings.json`; Codex roles use the `[agents]` defaults in `.codex/config.toml`. Pass an explicit session model and effort at spawn. Role files carry no model pin that can defeat that choice. Claude role effort defaults remain in frontmatter. Main-session and CLI review defaults remain in host configuration.

## The ticket loop

The steps and gates are in `AGENTS.md`, "Workflow for every ticket". This is how each one runs.

**Before step 1: scope and brief.** Read the ticket (`gh issue view <n> --comments`) and record the base commit (`git rev-parse HEAD`). Apply the scope and early external-probe rules in `AGENTS.md` before full implementation. `/kit-review` fetches the ticket itself, so no spec file is needed for it.

**Steps 1–2: implement, then focused tests.** Choose the task shape from `AGENTS.md`, "Operating model". A clear, small delegated fix uses one bounded worker; a normal ticket uses `kit-implementer` with the brief. Check its report against the tree: read the diff and the `verify` output it reports. Re-run a check only when that leaves a doubt; the current models verify their own work, and a second run of everything is wasted time.

**Step 3: review, sized to the ticket.** Use the review size from `AGENTS.md`. When one reviewer is required, give `kit-reviewer` the base and the spec. For dual review, the saved workflow in Claude Code is:

```text
Run /kit-review with base <base commit> and ticket 42
```

For an explicit Codex model instruction, pass it through, for example `codexModel gpt-6.1-sol`. The workflow's Claude agents use host defaults. If the session restricts all agents to one provider, run two independent reviewer agents in that host with the required model instead of invoking this cross-provider workflow. Apply the same review criteria and limits from `AGENTS.md`.

The work to review must be committed: the review reads one commit, never uncommitted changes. Pass `spec` (a file path, absolute or relative to the repository root) instead of `ticket` only for work with no ticket; exactly one of the two. Optional arguments: `head` (the commit to review; by default the commit your checkout is on when the run starts), `scope` (one line of plain text), `codexModel` (an explicit Codex model; omitted uses its host configuration), `codexEffort` (explicit `high` or `xhigh`; omitted uses host configuration), and `previous` with `rescore` for a re-review (step 4). A wrong combination fails at once and says what to pass.

The workflow resolves `base` and `head` to hashes, saves the ticket's title and body as `spec.md` in a run directory under the system temp directory, and checks `head` out as a detached worktree under `.claude/worktrees/kit-review-<random>`. `kit-reviewer` and a Codex inspection then review that worktree in parallel, and one verification stage merges duplicate findings and checks each against the code. `kit-reviewer` may build and run tests there. When the run ends, on success or failure, the worktree is removed with `git worktree remove`. Your checkout is never read for the diff, so you can keep working, merging and running implementers during a review. A changed submodule or nested repository in the diff still aborts the Codex leg.

The value returned is a digest that a task notification shows in full: it stays under about 5.5 KB whatever the number of findings. It starts with `complete`, `verdict`, `result` and, when needed, `cleanup` and `error`, then `counts` and each leg's verdict and one-line `note`. A re-review adds `rescored`, which maps each earlier finding's id to its state. Then come the findings, standing ones first and most severe first, each with only `id`, `severity`, `status`, `path`, `by` (who raised it) and a `claim` of at most 160 characters. When they do not all fit, the claims shrink to 100 and then 60 characters, and after that the list stops where the budget ends, with `omitted` giving how many more are in the file. `result` names the file with the full result: `<run directory>/result.json`, holding every finding's evidence, fix and verification, and each leg's read count and limitations. Read that file when you need more than the digest. If the file could not be written, `result` is null and the full result comes back under `full`, at the end; if the worktree could not be removed, `cleanup` gives the command.

Each finding has a stable `id` (`F1`, `F2`, …) and a status of `confirmed`, `refuted` or `unverified`. `pass`, `fix` and `block` are computed from the findings that were not refuted: `block` when one of them is high, `fix` when any stands, `pass` when none does.

`unknown` is never a pass. It means a leg or the verification stage did not return a usable review. A leg counts only when it reports that it ran, gives a verdict that means "reviewed" (`block`, `fix` or `pass` from `kit-reviewer`; `APPROVED` or `REVISE` from Codex), read at least one changed file and left none of the diff unread, and its verdict fits its findings. The leg's `note` says which of these failed. When the Codex runner fails, the note quotes the runner's own reason, for example "Selected model is at capacity". The workflow does not retry it.

The main session judges the findings; the workflow edits nothing in the reviewed commit.

The Codex leg has about 9.5 minutes, because a foreground shell call is capped at 10. When it runs out, the verdict is `unknown`. Run that leg yourself in the background against a fresh worktree of the same commit, using the `spec.md` from the run directory named in `result`, and judge its findings together with the `kit-reviewer` leg:

```bash
git worktree add --detach .claude/worktrees/kit-review-codex <head>
python3 .agents/skills/claudex-loop/scripts/runner.py inspect --host claude --builder claude \
  --repo .claude/worktrees/kit-review-codex --plan <run directory>/spec.md --base <base> \
  --timeout 1800
git worktree remove .claude/worktrees/kit-review-codex
```

The runner's first output line names an artifacts directory; `result.json` there holds the verdict, findings, coverage and limitations. Omitted `--model` and `--effort` use the provider's host configuration. Add them when the session specifies overrides.

From a Codex-hosted session, spawn the `kit-reviewer` agent for the Codex review and run the Claude inspection through the same runner, on a worktree of the commit as above, with the ticket saved as a spec file outside the checkout:

```bash
gh issue view 42 --json title,body --jq '"# " + .title + "\n\n" + .body' > /tmp/kit-ticket-42.md
python3 .agents/skills/claudex-loop/scripts/runner.py inspect --host codex --builder codex \
  --repo .claude/worktrees/kit-review-codex --plan /tmp/kit-ticket-42.md --base <base>
```

**Step 4: fixes and re-scores.** Follow the review limits in `AGENTS.md`. Give accepted findings to `kit-implementer` as a new brief, then have `kit-reviewer` re-score them from fresh evidence.

Only after a high finding was fixed and committed, and only once per ticket, run `/kit-review` again with the same `base` and `ticket`, and `previous` set to the earlier result file:

```text
Run /kit-review with base <base commit>, ticket 42, previous <earlier result file> and rescore F1,F3
```

`rescore` is optional: leave it out to re-score every earlier finding that was not refuted, or list the ids you accepted. `kit-reviewer` re-scores each of them, and Codex inspects the whole change fresh. If the earlier findings do not all reach the reviewer, the run stops with `unknown` before reviewing.

The digest's `rescored` map gives each of them one state: `resolved`, `partial` or `unresolved`. A finding the reviewer did not re-score counts as `unresolved`. Each `partial` or `unresolved` one still stands under its earlier `id`, at no less than its earlier severity, and the full result shows its re-score under `previous`; a finding new in this round gets the next `F` number after the highest one any earlier round used, so an id is never reused.
**Step 5: complete runtime proof.** The main session checks the real-service probe evidence and runs any remaining proof commands required by the ticket.

**Steps 6–7: docs and AI guidance.** The value, cost and impact paragraph goes in the ticket's closing comment, whose other contents `docs/agents/running-a-spec.md` lists; `/archify-review` runs at the wave's close-out. After updating the AI guidance, run the check script (below).

## The main session in Claude Code

**Output style.** `.claude/output-styles/kit-orchestrator.md`, selected by `outputStyle` in `.claude/settings.json`, puts the main session in the judge-and-orchestrator role and says how to choose between a role subagent, an agent team and `/kit-review`. Claude Code applies an output style to the main conversation and its forks only; role subagents never see it, which is why `CLAUDE.md` stays a bare import. Switch styles for yourself with `/output-style`.

**Agent teams.** `.claude/settings.json` enables them with `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`. Set the variable to `0` in `.claude/settings.local.json` (your own, untracked file) to turn them off for yourself. A session has one team, and teammates are not restored on resume.

**Status line.** `statusLine` runs `.claude/statusline.sh`, which prints one line for the main session, such as `Opus 5.5 · xhigh · ctx 42% · 5h 31% · main · kit-orchestrator`: model, effort, context use, the five-hour limit, branch, and the output style when it is not the default. A part the payload does not carry is left out, and `ctx` turns red from 80%. A project `statusLine` replaces the user-level one, so the script first passes the same payload to the `statusLine` command in your own user settings and prints whatever that prints above its own line; an integration that lives in the user's own status line command keeps working. It needs `jq`: without it the line is blank and the user's own status line command is not run.

**Subagent worktrees.** Claude Code creates each subagent worktree itself under the gitignored `.claude/worktrees/`, on a new branch, and removes it the usual way: at once if the agent changed nothing, otherwise by its periodic sweep. `"worktree": {"baseRef": "head"}` in `.claude/settings.json` bases it on the commit the session's checkout is on at spawn, instead of the remote's default branch; uncommitted changes do not carry over. The check script fails without it. To turn it off for yourself, put `{"worktree": {"baseRef": "fresh"}}` in `.claude/settings.local.json`. Codex creates no per-subagent worktree.

**Subagent rows.** `subagentStatusLine` is an inline `jq` command that renders each row of the agent panel as `kit-implementer · opus-5-5 · high · 24% ctx`, leaving out a field the task does not report. Without `jq` the default rows show.

**The `kit-cockpit` mod.** A mod is a plugin whose handlers run inside Claude Code. This one does two things:

- It draws one dim line above the prompt, such as `agents: kit-implementer · guard: 2 blocked, 1 overridden`. Each part appears only when there is something to show; the blocked count includes denies inside subagents. Figures the status line carries are not repeated here.
- It puts a guard deny to you as a question (see "The guard").

It loads as `kit-cockpit@skills-dir` through the `.claude/skills/kit-cockpit` symlink, once you have accepted the workspace trust dialog and only when the session starts in the repository root. To turn it off for yourself, set `"kit-cockpit@skills-dir": false` under `enabledPlugins` in `.claude/settings.local.json`. After editing it, run:

```bash
claude plugin validate .claude/mods/kit-cockpit
claude plugin test .claude/mods/kit-cockpit
```

A 30-second live check: start `claude` in the repository root and ask it to run `rm -rf ./no-such-directory`. The question "Keep blocked / Run it once" should appear, and after you keep it blocked the band should read `guard: 1 blocked`.

## The guard

`.agents/hooks/guard.py` runs before tool calls in both tools, including inside subagents. The script only denies, with a reason, or allows. In Claude Code the `kit-cockpit` mod turns a denied shell command of the main session into the question "Keep blocked / Run it once"; "Run it once" allows that one call. File-tool denies and calls inside subagents stay denied, and so does everything in Codex and in `claude -p`.

The guard is a seatbelt against accidents, not a sandbox. It stops the common destructive slip; it does not contain a session that sets out to get around it.

| Rule | Denied | Allowed |
|---|---|---|
| Writes stay in the working tree | A file-tool write (Claude Code `Edit`, `Write`, `NotebookEdit`; Codex `apply_patch`) whose target is outside the repository | The repository, system temp directories, `~/.claude/`, `~/.codex/` |
| Secret files | A file-tool read or write of `.env`, `.env.*`, `*.pem`, `*.key`, judged on the requested name and on the symlink target. Claude Code: `Read`, `Grep` whose `path` or `glob` names one, `Edit`, `Write`, `NotebookEdit`. Codex: `apply_patch` | Templates such as `.env.example` |
| Recursive delete | `rm -r`, `find -delete`, `find -exec rm`, `git clean -d` | One form only: a direct `rm -r` (optionally under `sudo`, `env` and the like) whose every argument is an option or a literal absolute path under a system temp directory, with no `--`. Never through `xargs` |
| Destructive push | `git push` with `--force`, `-f`, `--mirror`, `--delete`, `-d`, `+refspec` or `:branch` | `--force-with-lease` |

The first rule is how "never change the reference projects" is enforced without naming their locations. `.claude/settings.json` also sets an `ask` rule for `git push` in Claude Code.

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
```

It needs Python 3.11 or later, prints one `FAIL` line per problem naming the file and the reason, and exits non-zero on any. A rule glob that matches no file yet is a `WARN`. Besides the AI setup it fails on a merge conflict marker in any tracked text file. The `ai-setup` job in CI runs it on every push and pull request.

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
| Changes to user-level config | Three edits the owner approved, and nothing else: a key Claude Code no longer reads (`teammateDefaultModel`) and a key Codex ignores (`agents.max_depth`) were removed, and this repository was trusted in Codex. |

## Known gaps

- `kit-review` ran end to end on 2026-10-06 on ticket #4 (`base 43ff9ff`, `head 65d21b3`). It fetched the ticket by number, both legs ran, three findings were confirmed, the digest came back whole, `result.json` was written, and the review worktree was removed. A re-review of the same ticket ran the same day, with `previous` naming the first result file and `rescore F1,F3`: both came back `unresolved` under their earlier ids. Not yet taken in a live run: a failed leg, a result too large for the digest, and the finish step copying a large result byte for byte. Those paths were exercised only with stubbed agents.
- A script cannot run commands, so `kit-review` uses two small agents: one to resolve the commits, fetch the ticket and create the worktree, and one to write the full result file and remove the worktree. The second copies the result as text; the workflow checks its byte count and returns the full result itself when the count is wrong. A run that is stopped by hand skips the cleanup: remove its worktree with `git worktree remove --force .claude/worktrees/kit-review-<random>`.
- `/kit-review` supplies the cross-provider dual review when workflow step 3 requires it; a single reviewer or session-directed same-provider pair follows `AGENTS.md`. The vendored `code-review` skill, which reviews along a Standards axis and a Spec axis, is not part of the ticket gate.
- The guard was exercised with Codex-shaped payloads through the check script, not inside a live Codex session. Whether Codex loads `.codex/agents/*.toml` could not be observed without a model call.
- Mods are early access (Claude Code 2.1.287 or later), and their API may change between releases; re-run the two `claude plugin` commands after an upgrade. `kit-cockpit` passes them and loads, but it has not run in a live session. Unconfirmed: that the guard's deny reaches the mod as a `PreToolUse` deny with the marker still in its reason (without it the mod asks nothing and the deny stands), that the question appears with permission prompts off and "Run it once" then runs the command, what the band shows for role subagents and teammates, and hot reload. The 30-second check settles the first two.
- The status line script was tested against mock payloads and a stub user command, not in a live session and not against a live feed to the user's own status line command. Whether `statusLine` resolves the script when a session starts in a subdirectory depends on Claude Code loading the project settings there; the command falls back to the repository root through `git`.
- The subagent row command was tested against mock payloads, not a live agent panel.
- `worktree.baseRef` is not yet confirmed by a spawn: a subagent worktree created on 2026-10-06 still started at the remote's default branch. After the first spawn from a session whose checkout has the setting, the new row in `git worktree list` must show the checkout's commit.
- Four vendored skills (`blast-radius`, `how`, `interrogate`, `unslop`) set `disable-model-invocation` and have no `agents/openai.yaml`, so Codex can invoke them implicitly. Fixing that means editing vendored skills.
- The vendored `implement` skill says to use `/tdd` where possible. The workflow in `AGENTS.md` overrides it: implement first, then focused tests.
