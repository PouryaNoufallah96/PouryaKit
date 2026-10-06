#!/bin/sh
# Main-session status line (POSIX sh + jq), wired from statusLine in .claude/settings.json.
# A project statusLine replaces the user-level one, so the payload goes to the user's own
# statusLine command first and whatever that prints is passed through. Always exits 0: a
# status line that fails goes blank.
payload=$(cat)
[ -z "${POURYAKIT_STATUSLINE-}" ] || exit 0 # reached through our own forward: no second line
command -v jq >/dev/null 2>&1 || exit 0

user_command=$(jq -r '.statusLine | select(.type == "command") | .command // empty' \
  "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/settings.json" 2>/dev/null)
if [ -n "$user_command" ]; then
  user_output=$(printf '%s\n' "$payload" | POURYAKIT_STATUSLINE=1 sh -c "$user_command" 2>/dev/null || :)
  [ -z "$user_output" ] || printf '%s\n' "$user_output"
fi

field() { printf '%s' "$payload" | jq -r "$1 // empty" 2>/dev/null; }
branch=$(field '.worktree.branch?')
if [ -z "$branch" ]; then
  dir=$(field '.workspace.current_dir?')
  [ -z "$dir" ] || branch=$(git -C "$dir" branch --show-current 2>/dev/null)
fi

line=$(printf '%s' "$payload" | jq -r --arg branch "$branch" '
  def text: strings | select(. != "");
  def pct: floor | tostring + "%";
  [ ((.model.display_name | text)? // ((.model.id | text | sub("^claude-"; ""))? // empty)),
    (.effort.level // empty | tostring | text)?,
    (.context_window.used_percentage | numbers
      | if . >= 80 then "\u001b[31mctx \(pct)\u001b[0m" else "ctx \(pct)" end)?,
    (.rate_limits.five_hour.used_percentage | numbers | "5h \(pct)")?,
    ($branch | text),
    (.output_style.name | text | select(ascii_downcase != "default"))?
  ] | join(" · ")' 2>/dev/null)
[ -z "$line" ] || printf '%s\n' "$line"
exit 0
