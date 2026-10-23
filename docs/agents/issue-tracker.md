# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`. Use a heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Make an issue a sub-issue of a parent**: `gh issue create --parent <parent> ...`, or `gh issue edit <parent> --add-sub-issue <child>` afterwards (`gh` 2.94+). Older `gh`: `gh api --method POST repos/<owner>/<repo>/issues/<parent>/sub_issues -F sub_issue_id=<child-db-id>` (database id, as in **Blocking** below). Without sub-issues, put `Part of #<parent>` at the top of the child body.
- **Every issue created during a spec implementation run is a direct sub-issue of that spec**, flat: a work package, a fix round and a tracking or record issue all sit beside the spec's tickets, never nested under one another. Create it with `--parent <spec>`, and verify the relation before reporting (owner's rule, 2026-10-10). A Wayfinder map's tickets are the map's children instead ([Wayfinding operations](#wayfinding-operations)).
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`; a record kept in a file goes in as `--comment "$(cat <file>)"`, since `gh issue close` has no `--comment-file`

Infer the repo from `git remote -v`; `gh` does this automatically when run inside a clone.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents:

- **Read a PR**: `gh pr view <number> --comments` and `gh pr diff <number>` for the diff.
- **List external PRs for triage**: `gh api --paginate 'repos/{owner}/{repo}/pulls?state=open' --jq '.[] | select(.author_association | IN("OWNER","MEMBER","COLLABORATOR") | not) | {number, title, author: .user.login, author_association, labels: [.labels[].name]}'`.
- **Comment / label / close**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either: resolve with `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## Spec tickets

When `/to-tickets` starts from an existing spec issue, attach every published ticket to that spec as a native GitHub sub-issue. Add native blocking dependencies separately according to the approved graph; a parent link does not mean the spec blocks its tickets.

- **Attach to the spec**: as in **Make an issue a sub-issue of a parent** above, with the spec as the parent. Its REST form takes the ticket's numeric database id: `gh api repos/<owner>/<repo>/issues/<ticket> --jq .id`. The native flags were exercised on this tracker with `gh` 2.102.0 on 2026-10-09: `gh issue create --parent`, then `gh issue edit --remove-sub-issue` and `--add-sub-issue`, each read back from `issues/<parent>/sub_issues`.
- **Block**: `gh api --method POST repos/<owner>/<repo>/issues/<ticket>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, one call per blocker. Both endpoints want integer ids: `-F` sends a number, `-f` sends a string and is refused with 422; `--input -` with `{"issue_id": <id>}` works the same. In zsh, split a `"<ticket> <blocker>"` pair with `${pair% *}` and `${pair#* }` (an unquoted `$pair` does not word-split there).
- **Preserve the spec**: keep its title, body and open/closed state unchanged. Attaching approved children is authorized by ticket publication and is not prohibited by the vendored skill's parent-modification restriction.
- **Verify publication**: read back `issues/<spec>/sub_issues`, each ticket's labels and `issues/<ticket>/dependencies/blocked_by`. Every approved ticket must appear under the intended spec, carry the required triage label and have exactly its approved blockers. Preserve existing unrelated children. Textual parent references and blocker lists alone do not prove native relationships.

Use the native blocking operation documented below. If a native relationship is unavailable, retain its textual reference in the ticket and report that limitation rather than claiming it was linked.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets.

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body. `gh issue create --label wayfinder:map`.
- **Child ticket**: an issue linked to the map as a GitHub sub-issue (see **Make an issue a sub-issue of a parent**). Where sub-issues aren't enabled, add the child to a task list in the map body and put `Part of #<map>` at the top of the child body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: GitHub's **native issue dependencies**, the canonical, UI-visible representation. Add an edge with `gh api --method POST repos/<owner>/<repo>/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`, where `<blocker-db-id>` is the blocker's numeric **database id** (`gh api repos/<owner>/<repo>/issues/<n> --jq .id`, _not_ the `#number` or `node_id`). GitHub reports `issue_dependencies_summary.blocked_by` (open blockers only, the live gate). Where dependencies aren't available, fall back to a `Blocked by: #<n>, #<n>` line at the top of the child body. A ticket is unblocked when every blocker is closed.
- **Frontier query**: list the map's open children (`gh issue list --state open`, scoped to the map's sub-issues / task list), drop any with an open blocker (`issue_dependencies_summary.blocked_by > 0`, or an open issue in the `Blocked by` line) or an assignee; first in map order wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far.
