---
name: worktree-and-check-gotchas
description: Worktree base may lag feat/wave-1; shell compounds get refused; skill frontmatter cannot contain ": "
metadata:
  type: feedback
---

- The implementer worktree was created from an older commit than `feat/wave-1` (seen 2026-10-05). Always run `git merge-base --is-ancestor feat/wave-1 HEAD` first and reset onto it if it fails.
- The worktree isolation refuses compound shell commands (heredocs, `cd x && mkdir && cat <<EOF`, gh with `--jq` filters containing `\(`). Write files with the Write tool and keep each Bash call plain.
- `check-ai-setup.py` parses frontmatter with a tiny YAML subset: a plain scalar containing `": "`, `" #"`, or ending in `:` fails. Write skill descriptions without colons.
- No pyyaml/actionlint on the machine: `ruby -ryaml` parses YAML; `python3 -m venv <scratchpad>/venv && pip install actionlint-py` gives actionlint.

**Why:** each cost a retry during ticket #4.
**How to apply:** at the start of any package touching the worktree, skills or workflows.
