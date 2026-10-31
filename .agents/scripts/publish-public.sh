#!/bin/bash
# Publishes the reviewed public tree to the public repository as a new commit and tag. The owner runs this.
# Usage:     bash .agents/scripts/publish-public.sh <new tag, assets-v and a number> [--images]
# Rehearsal: PUBLIC_REMOTE=<bare repository> bash .agents/scripts/publish-public.sh <new tag> [--images]
#
# The tag the READMEs pin (ASSETS_TAG in addresses.ts) stays where it is, protected: it is where the images live, since
# the tree holds none, so a clone of the default branch fetches the skill alone. Each new release gets its own tag,
# because --verify-remote needs the default branch to equal the tag it reads. The commit, the parent and every other
# tag come from the checkout and the remote: nothing here is pinned, so a release needs no edit of this file.
#
# --images publishes a new or changed cover. Raise ASSETS_TAG and the READMEs' cover URLs first: a tag never moves, so
# every published URL keeps its image. The run adds that tag beside the default branch, on a commit holding only assets/,
# in the same atomic push as the release, so the branch stays small and the two land together or not at all.
#
# Why each line is here (#184, #192):
# - The push comes from a fresh clone of the public repository, never from the private checkout, which holds
#   refs/notes/ai. GIT_TRACE2_EVENT=0 is inherited by every git call, so git-ai's daemon neither writes nor pushes a note.
# - The remote may hold only main and assets tags, and the new tag must be new: a notes ref or anything else stops the
#   run before anything is written. The push is a fast-forward with explicit refspecs, never --tags or --force.
# - A public commit carries neither the personal identity nor a Claude session link or id (owner, 2026-10-31); the raw
#   commit is read before the push, and through the anonymous API after it.
# - verifyRemote reads only HEAD and the tag, so the refs are read separately, three times across 40 s, because the
#   daemon's push once landed 7 s after the commit.
# - Everything a release needs is checked before the push: the images are read from the pinned tag's tree in step 1, or
#   from the commit that adds them. What follows the push only reads back what landed.
set -euo pipefail

kit="$(cd "$(dirname "$0")/../.." && pwd)"
manifest="$kit/website/distribution/manifest.sha256"
address() { sed -n "s/^.*$1 = '\(.*\)';\$/\1/p" "$kit/website/lib/kit-sources/addresses.ts"; }
remote="${PUBLIC_REMOTE:-https://github.com/$(address PUBLIC_REPOSITORY).git}"
pinned_tag="$(address ASSETS_TAG)"
tag="${1:-}"
new_images="${2:-}"

# A machine with no global identity has nothing personal to look for, and an empty pattern would match every commit.
personal_email="$(git config --global user.email || true)"
personal_name="$(git config --global user.name || true)"
personal_email="${personal_email:-no-personal-email.invalid}"
personal_name="${personal_name:-no-personal-name.invalid}"

export GIT_TRACE2_EVENT=0
export TZ=UTC
export GIT_AUTHOR_NAME=PouryaNoufallah96
export GIT_AUTHOR_EMAIL=49477528+PouryaNoufallah96@users.noreply.github.com
export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME"
export GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"

finished=no
pushed=no
work=""

stop() {
  code="$1"
  shift
  finished=yes
  echo "STOP: $*" >&2
  exit "$code"
}

# A step that fails on its own still ends on a STOP line, so a run can never look finished when it is not.
trap '[ -z "$work" ] || rm -rf "$work"
if [ "$finished" != yes ]; then
  if [ "$pushed" = yes ]; then echo "STOP: a step ended early; the release WAS pushed, tell Claude" >&2
  else echo "STOP: a step ended early; nothing was pushed" >&2; fi
fi' EXIT

anonymously() {
  env -u GH_TOKEN -u GITHUB_TOKEN GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_TERMINAL_PROMPT=0 "$@"
}

holds_private() {
  printf '%s' "$1" | grep -qiF -e "$personal_email" -e "$personal_name" -e 'Claude-Session' -e 'session_' -e 'claude.ai/code'
}

# Every image a README pins, in byte order.
images() {
  {
    printf '%s\n' assets/kit.png assets/icon.png
    jq -r '.packages[].id | "assets/covers/\(.).png"' "$kit/scripts/packaging/inventory.json"
  } | LC_ALL=C sort
}

[[ $# -le 2 && "$tag" =~ ^assets-v[0-9]+$ && "$new_images" =~ ^(--images)?$ ]] ||
  stop 64 "usage: publish-public.sh <new tag, assets-v and a number> [--images]"

echo "1/6 the remote holds only main and assets tags, and not $tag, and the images are in place"
existing="$(anonymously git ls-remote "$remote")"
parent="$(printf '%s\n' "$existing" | awk '$2 == "refs/heads/main" { print $1 }')"
strays="$(printf '%s\n' "$existing" | awk '$2 != "HEAD" && $2 != "refs/heads/main" && $2 !~ /^refs\/tags\/assets-v[0-9]+$/')"
[ -n "$parent" ] && [ -z "$strays" ] || stop 2 "the remote holds something else; nothing was pushed:
$existing"
if [ -n "$new_images" ]; then
  if printf '%s\n' "$existing" | grep -q "refs/tags/$pinned_tag\$"; then
    stop 2 "$pinned_tag already exists and a tag never moves: raise ASSETS_TAG; nothing was pushed"
  fi
  [ "$tag" != "$pinned_tag" ] || stop 2 "the release and the images need a tag each; nothing was pushed"
else
  printf '%s\n' "$existing" | grep -q "refs/tags/$pinned_tag\$" || stop 2 "the remote has no $pinned_tag; nothing was pushed"
fi
if printf '%s\n' "$existing" | grep -q "refs/tags/$tag\$"; then stop 2 "$tag already exists; nothing was pushed"; fi

work="$(mktemp -d)"

if [ -z "$new_images" ]; then
  anonymously git clone -q --filter=blob:none --depth 1 --no-checkout --branch "$pinned_tag" "$remote" "$work/pinned"
  missing="$(LC_ALL=C comm -23 <(images) <(git -C "$work/pinned" ls-tree -r --name-only HEAD | LC_ALL=C sort))"
  [ -z "$missing" ] || stop 8 "$pinned_tag lacks these images, so raise ASSETS_TAG and add --images; nothing was pushed:
$missing"
fi

echo "2/6 the checkout is clean and equals origin/main"
[ -z "$(git -C "$kit" status --porcelain)" ] || stop 10 "the checkout has changes; nothing was pushed"
if [ -z "${PUBLIC_REMOTE:-}" ]; then
  [ "$(git -C "$kit" ls-remote origin refs/heads/main | cut -f1)" = "$(git -C "$kit" rev-parse HEAD)" ] ||
    stop 10 "the checkout is not origin/main; nothing was pushed"
fi

echo "3/6 the tree is regenerated and matches the committed manifest"
pnpm --dir "$kit/website" install --frozen-lockfile >"$work/install.log"
if [ -n "$new_images" ]; then
  pnpm --dir "$kit/website" covers >"$work/covers.log"
  [ -z "$(git -C "$kit" status --porcelain)" ] || stop 10 "the covers changed a tracked file; nothing was pushed"
fi
pnpm --dir "$kit/website" sync-public --check | tail -1
pnpm --dir "$kit/website" sync-public "$work/tree" >"$work/tree.log"

echo "4/6 the commit is built in a fresh clone and read before the push"
clone="$work/clone"
git clone -q --single-branch --branch main --no-tags "$remote" "$clone"
[ "$(git -C "$clone" rev-parse HEAD)" = "$parent" ] || stop 2 "the remote moved during this run; nothing was pushed"
was="$(git -C "$clone" show HEAD:.claude-plugin/marketplace.json | jq -r '.plugins[0].version')"
git -C "$clone" rm -rq .
cp -R "$work/tree/." "$clone/"
git -C "$clone" add -A
now="$(jq -r '.plugins[0].version' "$clone/.claude-plugin/marketplace.json")"
git -C "$clone" diff --cached --quiet && stop 9 "the public repository already holds this tree; nothing was pushed"
if ! git -C "$clone" diff --cached --quiet -- skills && [ "$was" = "$now" ]; then
  stop 9 "the skill changed but its version is still $now: bump metadata.version, then rewrite the manifest"
fi

(cd "$clone" && shasum -a 256 -c --quiet "$manifest") || stop 7 "a file differs from the manifest; nothing was pushed"
published="$(git -C "$clone" ls-files | LC_ALL=C sort)"
listed="$(sed -E 's/^[0-9a-f]{64} [ *]//' "$manifest" | LC_ALL=C sort)"
[ "$published" = "$listed" ] || stop 7 "the file list differs from the manifest; nothing was pushed"
printf '%s\n' "$published" | grep -qE '(^|/)(\.vscode|public)(/|$)' && stop 7 "an incident path is in the tree; nothing was pushed"

git -C "$clone" commit -q -m "feat: the skill at $now"

commit="$(git -C "$clone" rev-parse HEAD)"
holds_private "$(git -C "$clone" cat-file -p "$commit")" &&
  stop 3 "the commit carries the personal identity or a session link; nothing was pushed"
[ -z "$(git -C "$clone" for-each-ref refs/notes)" ] || stop 4 "a notes ref exists locally; nothing was pushed"
git -C "$clone" tag --no-sign "$tag" "$commit"

refs=("refs/heads/main:refs/heads/main" "refs/tags/$tag:refs/tags/$tag")
if [ -n "$new_images" ]; then
  mkdir -p "$work/images/assets"
  cp -R "$kit/website/covers/out/." "$work/images/assets/"
  cp "$kit/src/icon.png" "$work/images/assets/icon.png"
  (cd "$work/images" && GIT_INDEX_FILE="$work/images.index" git --git-dir="$clone/.git" --work-tree=. add -A)
  images_tree="$(GIT_INDEX_FILE="$work/images.index" git -C "$clone" write-tree)"
  images_commit="$(git -C "$clone" commit-tree "$images_tree" -p "$parent" -m "feat: the images at $pinned_tag")"
  [ "$(git -C "$clone" ls-tree -r --name-only "$images_commit" | LC_ALL=C sort)" = "$(images)" ] ||
    stop 7 "the typeset images differ from the inventory; nothing was pushed"
  holds_private "$(git -C "$clone" cat-file -p "$images_commit")" &&
    stop 3 "the images commit carries the personal identity or a session link; nothing was pushed"
  git -C "$clone" tag --no-sign "$pinned_tag" "$images_commit"
  refs+=("refs/tags/$pinned_tag:refs/tags/$pinned_tag")
fi

echo "5/6 pushing ${commit:0:8} to main and $tag${new_images:+, with the images at $pinned_tag}, all or nothing"
git -C "$clone" push -q --atomic origin "${refs[@]}"
pushed=yes
rm -rf "$clone"

echo "6/6 reading it back as an anonymous client (about a minute)"
expected="$(
  printf '%s\n' "$existing" | awk -v commit="$commit" '$2 == "HEAD" || $2 == "refs/heads/main" { print commit "\t" $2; next } { print }'
  printf '%s\trefs/tags/%s\n' "$commit" "$tag"
  [ -z "$new_images" ] || printf '%s\trefs/tags/%s\n' "$images_commit" "$pinned_tag"
)"
expected="$(printf '%s\n' "$expected" | LC_ALL=C sort)"
for read_number in 1 2 3; do
  [ "$read_number" = 1 ] || sleep 20
  refs="$(anonymously git ls-remote "$remote" | LC_ALL=C sort)"
  [ "$refs" = "$expected" ] || stop 5 "read $read_number found other refs (PUSHED, tell Claude):
$refs"
  echo "    read $read_number: main at ${commit:0:8}, every tag kept, $tag added"
done

anonymously pnpm --dir "$kit/website" sync-public --verify-remote "$tag" --remote "$remote" | tail -1

case "$remote" in
  https://github.com/*)
    slug="${remote#https://github.com/}"
    slug="${slug%.git}"
    response="$(curl -fsS "https://api.github.com/repos/$slug/commits/$commit")" ||
      stop 6 "the commit API could not be read, so the public commit is unchecked (PUSHED, tell Claude)"
    holds_private "$response" &&
      stop 6 "the public commit shows the personal identity or a session link (PUSHED, tell Claude)"
    echo "    the commit API shows nothing personal or session-related"
    assets=0
    while read -r asset; do
      status=000
      for attempt in 1 2 3 4 5 6; do
        status="$(curl -sS -o /dev/null -w '%{http_code}' -I "https://raw.githubusercontent.com/$slug/$pinned_tag/$asset" || true)"
        [ "$status" = 200 ] && break
        sleep 5
      done
      [ "$status" = 200 ] || stop 8 "$asset answered $status (PUSHED, tell Claude)"
      assets=$((assets + 1))
    done < <(images)
    echo "    $assets images answer 200 anonymously at $pinned_tag, the tag the READMEs pin"
    ;;
esac

finished=yes
echo "PASS: $remote main and $tag are at $commit"
