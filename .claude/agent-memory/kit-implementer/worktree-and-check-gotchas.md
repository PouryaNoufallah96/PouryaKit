---
name: worktree-and-check-gotchas
description: Shell shapes the worktree isolation refuses, an integration branch that moves mid-package, gh issue view quirk, BSD sed, frontmatter colons, guard rm -r
metadata:
  type: feedback
---

The worktree base and the one-git-command rule are in the role file; these are what it does not say.

- **Refused shells beyond git:** heredocs that name git, long `python3 - <<'EOF'` heredocs, `cd` to a computed directory, `find … | xargs sed`, a `for` loop or `sed` with a computed argument, `grep` with backticks in the pattern, `docker --format '{{…}}'` in a compound line, and any `git -C` or `cd` into a reference repository or another agent's worktree. Write the script to the scratchpad with Write and run `python3 <file>`; read other trees with the Read tool by absolute path; resolve a reference repo's short SHA from its `.git/logs/HEAD`. In zsh, `for m in "a b"` does not word-split. A foreground `sh script.sh` that sleeps passes (timeout 600000) where a bare `sleep` is refused.
- **The integration branch moves mid-package** (it also carries memory commits). Check `git log HEAD..<integration branch>` before trusting `git diff --stat <integration branch>`, which then lists the tip's files as yours. `git merge --ff-only` works with uncommitted edits the tip does not touch; an untracked copy of a file the tip adds blocks the merge. After a merge, re-read the code the brief described and re-word any doc claim the tip made false. Check a base commit with `git branch -a --contains <sha>`, not by branch name, when another agent may reset that branch.
- The system-reminder's copy of `AGENTS.md` comes from the main checkout and can differ from the worktree's: read the worktree file before editing. A Write or Edit to the main checkout's path is refused; use the worktree path.
- `gh issue view N --comments` printed nothing from a worktree; `gh issue view N --repo PouryaNoufallah96/PouryaKit` plus `gh api repos/PouryaNoufallah96/PouryaKit/issues/N/comments` works.
- macOS BSD `sed -E` silently ignores `\b`; use `[[:<:]]word[[:>:]]`, then grep to confirm.
- `check-ai-setup.py` parses frontmatter with a small YAML subset: a plain scalar containing `": "` or `" #"`, or ending in `:`, fails. It reads frontmatter in skills' `SKILL.md` files, output styles, Claude roles and rules.
- The guard refuses `rm -r` outside system temp: delete files one by one, then `rmdir`. Mutation checks: copy the file to the scratchpad, mutate, run, copy back.
- The Edit tool validates `.claude/settings.json` against the live schema and refuses an invalid enum, so mutation-test `check-ai-setup.py` in memory (load the module, swap `loaded[path]`).
- No pyyaml or actionlint by default: `ruby -ryaml` parses YAML; `brew install actionlint` works.
- An `AsyncLocal` write inside an awaited async method never flows back to the caller, so asserting `DomainClock.Current` after awaiting the dispatcher cannot fail; test binding restore synchronously (`tests/ModularMonolith.Tests/TestClockTests.cs`).

**Why:** each cost a retry in Wave 1 tickets #4 to #19.
**How to apply:** when a shell command is refused, when the integration branch moves, or when touching skills, settings or the check script.
