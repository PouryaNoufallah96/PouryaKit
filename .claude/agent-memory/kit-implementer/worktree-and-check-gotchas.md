---
name: worktree-and-check-gotchas
description: Worktree base may lag feat/wave-1; shell compounds get refused; skill frontmatter cannot contain ": "
metadata:
  type: feedback
---

- The implementer worktree was created from an older commit than `feat/wave-1` (seen 2026-10-05). Always run `git merge-base --is-ancestor feat/wave-1 HEAD` first and reset onto it if it fails.
- The worktree isolation refuses compound shell commands (heredocs, `cd x && mkdir && cat <<EOF`, gh with `--jq` filters containing `\(`). Write files with the Write tool and keep each Bash call plain.
- Seen again in ticket #6: the worktree started at an unrelated old commit (`33fd974`), and `feat/wave-1` moved during the package, so merge its tip before the final verify.
- The isolation also refuses any `git -C <reference repo>` (and git inside a `for` loop). When the reference checkout's HEAD equals the pinned commit, read its files with the Read tool by absolute path instead.
- `gh issue view N --comments` printed nothing from the worktree; `gh issue view N --repo PouryaNoufallah96/PouryaKit` plus `gh api repos/PouryaNoufallah96/PouryaKit/issues/N/comments` works.
- `check-ai-setup.py` parses frontmatter with a tiny YAML subset: a plain scalar containing `": "`, `" #"`, or ending in `:` fails. Write skill descriptions without colons.
- No pyyaml/actionlint on the machine: `ruby -ryaml` parses YAML; `python3 -m venv <scratchpad>/venv && pip install actionlint-py` gives actionlint.

**Why:** each cost a retry during ticket #4.
**How to apply:** at the start of any package touching the worktree, skills or workflows.
