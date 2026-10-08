---
paths:
  - ".claude/**"
  - ".codex/**"
  - ".agents/**"
  - "AGENTS.md"
  - "CLAUDE.md"
  - "skills-lock.json"
---

# AI setup

The operator guide, with the reasons behind this layout and what was deliberately left out, is `docs/agents/ai-setup.md`. Read it before adding a new kind of file.

One home per concern: `.claude/` and `.codex/` hold only what each tool loads natively; anything both tools use lives in `.agents/` or at the root. The layout table is in the operator guide.

## When you change it

- **Role pairs stay identical.** Same `name`, same `description`, and the `.toml` `developer_instructions` is the `.md` body word for word. Change both files in the same edit.
- **A rule's frontmatter is `paths` only**, and every rule has a row in the `AGENTS.md` rule router.
- **Main-session-only guidance goes in the output style**, `.claude/output-styles/kit-orchestrator.md`. Role subagents load `CLAUDE.md` and `AGENTS.md` but never the output style, so `CLAUDE.md` stays a bare `@AGENTS.md` import.
- **The mod lives in `.claude/mods/kit-cockpit/`** and loads through its `.claude/skills/` symlink. It uses only names in the build's mod type declarations. After any edit, run `claude plugin validate` and `claude plugin test` on it.
- **Vendored skills stay as vendored.** `skills-lock.json` pins each one by hash. Update a skill by re-vendoring it from its upstream; put project-specific guidance in a rule or in `AGENTS.md`.
- **Verify every new key against the live doc page** before adding it: frontmatter field, settings key, hook event, config key (`https://code.claude.com/docs/en/<page>.md`, `https://learn.chatgpt.com/docs/<page>.md`). Both tools silently ignore a key they do not know, so a wrong one fails without an error.
- **Each role pins its model and effort** in its own file: Claude frontmatter `model` and `effort`, Codex `model` and `model_reasoning_effort`. A Claude spawn model still overrides that frontmatter. A Codex role file overrides a spawn model. `CLAUDE_CODE_SUBAGENT_MODEL` and `[agents]` cover a subagent that is not one of these roles. The table is in the operator guide, "Roles".
- **Review routing follows `AGENTS.md`.** Extend the saved `kit-review` for cross-provider dual review instead of adding another workflow. After editing a saved workflow, run `/reload-skills`.
- **The guard is frozen.** It is a seatbelt, not a sandbox. Change it only after a real incident, and review only the lines that changed.
- **Leave nothing dead.** In the same change, remove what it makes obsolete: a key the tool no longer reads, a file nothing loads, a doc line the code no longer matches. Keep `docs/agents/ai-setup.md` and the guard's docstring in step with the files they describe.
- **Finish with `python3 .agents/scripts/check-ai-setup.py` and `python3 .agents/scripts/check-docs.py`.** Both must exit 0.
