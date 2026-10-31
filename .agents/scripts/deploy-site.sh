#!/bin/bash
# Builds the docs site with the Vercel project's settings and deploys it to production. The owner runs this, on the
# Vercel login already on the machine: it never runs vercel login, logout or link.
# Usage:     bash .agents/scripts/deploy-site.sh
# Rehearsal: bash .agents/scripts/deploy-site.sh --rehearse     everything up to vercel deploy --dry; nothing is deployed
#
# Why each line is here:
# - The deploy runs from a stage outside any Git repository. Inside one, the CLI sends the origin URL (the private
#   repository), the commit's author name and email and its message (createGitMeta), and on a Hobby team an author
#   who is not the account owner gets the deployment refused. Outside one it sends none of it.
# - project.json is written by hand: vercel link may offer to connect Git, and no Vercel GitHub App reaches the private
#   repository (spec, D5).
# - check-export reads out/, so the stage must equal out/ file by file, or its verdict says nothing about the upload.
# - diagnostics/ and builds.json's argv and requirePath hold this machine's paths. No page serves them, but the CLI
#   uploads them. The CLI reads only target and error from builds.json, which is not part of the Build Output API.
# - _next/__private/trace is routed to a 404 and out/ does not hold it, so it leaves too.
# - The served llms.txt is compared with the build's, so a 200 from an older deployment cannot pass.
# - It prints each step, and it ends only on PASS or STOP: a run that prints neither did not deploy.
set -euo pipefail

kit="$(cd "$(dirname "$0")/../.." && pwd)"
address() { sed -n "s/^.*$1 = '\(.*\)';\$/\1/p" "$kit/website/lib/kit-sources/addresses.ts"; }
website="$kit/website"
project_id=prj_V382FKN7RxyG71wN0XDdEmGq6TlM
org_id=team_33wgPTpw8cq4tdmGWonoza9O
site="$(address SITE_ADDRESS)"
vercel_user=ultrabusiness
tag="$(address ASSETS_TAG)"
repository="$(address PUBLIC_REPOSITORY)"

rehearse=no
[ "${1:-}" = --rehearse ] && rehearse=yes

finished=no
deployed=no

stop() {
  code="$1"
  shift
  finished=yes
  echo "STOP: $*" >&2
  exit "$code"
}

personal_email="$(git config --global user.email || true)"
personal_name="$(git config --global user.name || true)"
personal_email="${personal_email:-no-personal-email.invalid}"
personal_name="${personal_name:-no-personal-name.invalid}"

stage="$(mktemp -d)"
log="$(mktemp -d)"
# A step that fails on its own still ends on a STOP line, so a run can never look finished when it is not.
trap 'rm -rf "$stage" "$log"; rm -f "$website/.vercel/.env.production.local"
if [ "$finished" != yes ]; then
  if [ "$deployed" = yes ]; then echo "STOP: a step ended early; the site WAS deployed, tell Claude" >&2
  else echo "STOP: a step ended early; nothing was deployed" >&2; fi
fi' EXIT

echo "1/7 the login is $vercel_user and the checkout is clean"
[ "$(vercel whoami 2>/dev/null)" = "$vercel_user" ] || stop 11 "vercel whoami is not $vercel_user; nothing was deployed"
[ -z "$(git -C "$kit" status --porcelain)" ] || stop 10 "the checkout has changes; nothing was deployed"

if [ "$rehearse" = yes ]; then
  echo "2/7 skipped in a rehearsal: the public repository is not read"
else
  echo "2/7 the checkout is origin/main, and the image tag the package pages show is public"
  [ "$(git -C "$kit" ls-remote origin refs/heads/main | cut -f1)" = "$(git -C "$kit" rev-parse HEAD)" ] ||
    stop 10 "the checkout is not origin/main: push it first; nothing was deployed"
  env -u GH_TOKEN -u GITHUB_TOKEN GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_TERMINAL_PROMPT=0 \
    git ls-remote --exit-code "https://github.com/$repository.git" "refs/tags/$tag" >/dev/null ||
    stop 12 "the public repository has no tag $tag; nothing was deployed"
fi

echo "3/7 the project's settings are pulled"
mkdir -p "$website/.vercel"
printf '{"projectId":"%s","orgId":"%s","projectName":"pouryakit"}\n' "$project_id" "$org_id" >"$website/.vercel/project.json"
(cd "$website" && vercel pull --yes --environment=production) >"$log/pull.log" 2>&1 ||
  { tail -20 "$log/pull.log" >&2; stop 12 "vercel pull failed; nothing was deployed"; }

echo "4/7 the site is built from clean (about a minute)"
pnpm --dir "$website" install --frozen-lockfile >"$log/install.log"
rm -rf "$website/.vercel/output" "$website/.next" "$website/out"
(cd "$website" && SITE_URL="$site" vercel build --prod --yes) >"$log/build.log" 2>&1 ||
  { tail -40 "$log/build.log" >&2; stop 13 "vercel build failed; nothing was deployed"; }
[ -z "$(git -C "$kit" status --porcelain)" ] || stop 10 "the build changed a tracked file; nothing was deployed"
(cd "$website" && node scripts/kit-content.ts check-export) | tail -1

echo "5/7 the stage is assembled outside Git, and equals what check-export read"
git -C "$stage" rev-parse --git-dir >/dev/null 2>&1 && stop 14 "the stage is inside a Git repository; nothing was deployed"
mkdir "$stage/.vercel"
cp -R "$website/.vercel/output" "$stage/.vercel/output"
cp "$website/.vercel/project.json" "$stage/.vercel/project.json"
rm -rf "$stage/.vercel/output/diagnostics" "$stage/.vercel/output/static/_next/__private"

python3 -I - "$website/out" "$stage/.vercel" "$personal_email" "$personal_name" <<'EOF' || stop 15 "the stage failed its check; nothing was deployed"
import hashlib, json, os, re, sys

out, vercel, email, name = sys.argv[1:]
output = os.path.join(vercel, "output")


def files(root):
    found = {}
    for directory, _, names in os.walk(root):
        for entry in names:
            path = os.path.join(directory, entry)
            found[os.path.relpath(path, root)] = path
    return found


def digests(root):
    return {relative: hashlib.sha256(open(path, "rb").read()).digest() for relative, path in files(root).items()}


builds_path = os.path.join(output, "builds.json")
builds = json.load(open(builds_path))
builds["argv"] = []
for build in builds.get("builds", []):
    build.pop("requirePath", None)
json.dump(builds, open(builds_path, "w"))

exported, staged = digests(out), digests(os.path.join(output, "static"))
if exported != staged:
    different = sorted(path for path in set(exported) | set(staged) if exported.get(path) != staged.get(path))
    sys.exit(f"the stage differs from out/ in {len(different)} files: {different[:5]}")

needles = [
    re.compile(re.escape(email.encode()), re.I),
    re.compile(re.escape(name.encode()), re.I),
    re.compile(rb"/Users/pourya"),
    re.compile(rb"/private/tmp/"),
    re.compile(rb"claude-session", re.I),
    re.compile(rb"github\.com/PouryaNoufallah96/PouryaKit(?!-pub)", re.I),
]
hits = []
for relative, path in files(vercel).items():
    if relative == "project.json":
        continue
    content = open(path, "rb").read()
    hits += [f"{relative}: {needle.pattern!r}" for needle in needles if needle.search(content)]
if hits:
    sys.exit("private strings in the stage:\n" + "\n".join(hits[:20]))
print(f"    {len(staged)} static files equal out/; no private string in the stage")
EOF

echo "6/7 vercel deploy --dry lists what would be uploaded"
(cd "$stage" && vercel deploy --prebuilt --prod --yes --dry) >"$log/dry.json" 2>"$log/dry.err" ||
  { tail -20 "$log/dry.err" >&2; stop 16 "vercel deploy --dry failed; nothing was deployed"; }
python3 -I - "$log/dry.json" <<'EOF' || stop 16 "the upload list is not what was staged; nothing was deployed"
import json, sys

raw = open(sys.argv[1]).read()
summary = json.loads(raw[raw.find("{"):])
paths = [entry["path"] for entry in summary["files"]]
allowed = {".vercel/output/builds.json", ".vercel/output/config.json"}
others = [path for path in paths if path not in allowed and not path.startswith(".vercel/output/static/")]
if others:
    sys.exit(f"unexpected uploads: {others[:10]}")
print(f"    {len(paths)} files, {summary['totalSize'] / 1e6:.1f} MB, only the static site and its routes")
EOF

if [ "$rehearse" = yes ]; then
  finished=yes
  echo "PASS: rehearsal; nothing was deployed"
  exit 0
fi

echo "7/7 deploying to production"
(cd "$stage" && vercel deploy --prebuilt --prod --yes) >"$log/deploy.log" 2>&1 ||
  { tail -30 "$log/deploy.log" >&2; stop 17 "vercel deploy failed; tell Claude"; }
deployed=yes
tail -4 "$log/deploy.log"

for path in / /docs/ /llms.txt /api/search.json /og/image.png; do
  status=000
  for attempt in 1 2 3 4 5 6 7 8 9 10 11 12; do
    status="$(curl -sS -o /dev/null -w '%{http_code}' "$site$path" || true)"
    [ "$status" = 200 ] && break
    sleep 5
  done
  [ "$status" = 200 ] || stop 18 "$site$path answered $status (DEPLOYED, tell Claude)"
  echo "    $path 200"
done
served="$(curl -fsS "$site/llms.txt" | shasum -a 256 | cut -d' ' -f1)"
built="$(shasum -a 256 "$website/out/llms.txt" | cut -d' ' -f1)"
[ "$served" = "$built" ] || stop 19 "$site serves another build's llms.txt (DEPLOYED, tell Claude)"

finished=yes
echo "PASS: $site serves this build"
