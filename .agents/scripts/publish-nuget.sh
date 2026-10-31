#!/usr/bin/env bash
# Publishes the validated originals of a release to nuget.org, by hand, without GitHub Actions. The owner runs this,
# only after approving the release. It never builds or packs: it verifies the manifest and every nupkg's SHA-256, checks
# that the commit and tag are on GitHub and that no package has this version yet, pushes each original with the standard
# client, waits until nuget.org lists every package,
# then proves the set from nuget.org alone with fresh caches: every library package restored at its exact version, the
# template package installed, and each template generated and restored under a canonical path.
#
# The version and the commit come from the manifest of the release directory, which validate.py --keep wrote.
# Run it in your own terminal, not through the agent:
#   bash .agents/scripts/publish-nuget.sh <release directory holding manifest.json and packages/>
# It asks for the nuget.org API key at a hidden prompt; the key stays in this process, never in a file or the history.
# Use a new key scoped to "Push new packages and package versions" with the glob PouryaKit.*, and revoke it afterwards.
set -euo pipefail
release=$(cd "${1:?usage: publish-nuget.sh <release directory>}" && pwd -P)
repo=$(cd "$(dirname "$0")/../.." && pwd -P)
expected_commit=$(jq -er '.commit' "$release/manifest.json")
expected_version=$(jq -er '.version' "$release/manifest.json")
count=$(jq '.packages | length' "$repo/scripts/packaging/inventory.json")
log="$(dirname "$release")/nuget-publication-$expected_version.log"
exec > >(tee -a "$log") 2>&1
finished=no
pushed=0
trap '[ "$finished" = yes ] || echo "STOP: the run ended early, after pushing $pushed packages; send this log to the session"' EXIT
source=https://api.nuget.org/v3/index.json
flat=https://api.nuget.org/v3-flatcontainer
tag=v$expected_version
private=https://github.com/PouryaNoufallah96/PouryaKit.git
echo "== $(date -u +%FT%TZ) nuget.org publication of $expected_version from $release"

# 1. The originals: one file per package of the inventory, named by ID and version, every SHA-256.
version=$expected_version
diff <(jq -r '.packages[].id' "$repo/scripts/packaging/inventory.json" | sort) <(jq -r '.packages[].id' "$release/manifest.json" | sort) > /dev/null \
  || { echo "FAIL: the manifest's packages are not the inventory's"; exit 1; }
jq -e --arg v "$version" 'all(.packages[]; .file == (.id + "." + $v + ".nupkg"))' "$release/manifest.json" > /dev/null
diff <(jq -r '.packages[].file' "$release/manifest.json" | sort) <(ls "$release/packages" | grep '\.nupkg$' | sort)
(cd "$release" && jq -r '.packages[] | .sha256 + "  packages/" + .file' manifest.json | shasum -a 256 -c - > /dev/null)
ids=$(jq -r '.packages[].id' "$release/manifest.json")
echo "Verified $count originals at $version for $expected_commit."

# 2. What is published must be what GitHub holds: main holds the commit, and the tag names it.
git -C "$repo" fetch -q origin refs/heads/main
git -C "$repo" merge-base --is-ancestor "$expected_commit" FETCH_HEAD || { echo "STOP: origin/main does not hold $expected_commit: push main first"; exit 1; }
remote_tag=$(git ls-remote "$private" "refs/tags/$tag" | cut -f1)
test "$remote_tag" = "$expected_commit" || { echo "STOP: $tag on GitHub is '$remote_tag', not $expected_commit: push the tag first"; exit 1; }

# 3. No package may have this version yet: nuget.org never lets a version be replaced. Only a 404 means a new ID.
versions=$(mktemp)
for id in $ids; do
  lower=$(tr '[:upper:]' '[:lower:]' <<< "$id")
  code=$(curl -s -o "$versions" -w '%{http_code}' "$flat/$lower/index.json" || true)
  case "$code" in
    404) ;;
    200)
      if jq -e --arg v "$version" '.versions | index($v) != null' "$versions" > /dev/null; then
        echo "STOP: $id already has $version on nuget.org"; exit 1
      fi ;;
    *) echo "STOP: nuget.org answered '$code' for $id, so its versions are unknown; nothing was pushed"; exit 1 ;;
  esac
done
rm -f "$versions"
echo "The commit and tag are on GitHub, and no package has $version yet."

read -rsp "nuget.org API key (hidden): " key; echo
test -n "$key" || { echo "FAIL: empty key"; exit 1; }
read -rp "Push the $count packages at $version to nuget.org now? This cannot be undone, only unlisted. Type yes: " answer
test "$answer" = yes || { unset key; echo "Stopped; nothing pushed."; exit 1; }

# 4. Push each original. The key is on the push's command line only, as the standard client requires for nuget.org.
for file in $(jq -r '.packages[].file' "$release/manifest.json"); do
  dotnet nuget push "$release/packages/$file" --source "$source" --api-key "$key" --skip-duplicate --no-symbols
  pushed=$((pushed + 1))
done
unset key
echo "Pushed $pushed packages. Revoke the API key now."

# 5. nuget.org validates each upload before it lists it; wait up to 60 minutes for them all.
deadline=$(( $(date +%s) + 3600 ))
for id in $ids; do
  lower=$(tr '[:upper:]' '[:lower:]' <<< "$id")
  until curl -sf "$flat/$lower/index.json" | jq -e --arg v "$version" '.versions | index($v) != null' > /dev/null; do
    test "$(date +%s)" -lt "$deadline" || { echo "FAIL: $id $version not listed after 60 minutes"; exit 1; }
    sleep 30
  done
done
echo "All $count packages are listed on nuget.org."

# 6. Content identity: nuget.org adds its repository signature, so compare every entry except the signature and the
# content-types part the signature changes.
work=$(cd "$(mktemp -d)" && pwd -P)
entries() { (cd "$1" && find . -type f ! -name '.signature.p7s' ! -name '[[]Content_Types].xml' | sort | xargs shasum -a 256); }
for file in $(jq -r '.packages[].file' "$release/manifest.json"); do
  id=${file%."$version".nupkg}; lower=$(tr '[:upper:]' '[:lower:]' <<< "$id")
  mkdir -p "$work/cmp/$id/original" "$work/cmp/$id/remote"
  curl -sf -o "$work/cmp/$id/remote.nupkg" "$flat/$lower/$version/$lower.$version.nupkg"
  unzip -q "$release/packages/$file" -d "$work/cmp/$id/original"
  unzip -q "$work/cmp/$id/remote.nupkg" -d "$work/cmp/$id/remote"
  test -f "$work/cmp/$id/remote/.signature.p7s" || { echo "FAIL: $id has no repository signature"; exit 1; }
  diff <(entries "$work/cmp/$id/original") <(entries "$work/cmp/$id/remote") > /dev/null \
    || { echo "FAIL: $id content differs from the original"; exit 1; }
done
echo "All $count downloaded packages carry nuget.org's repository signature and the originals' exact content."

# 7. From nuget.org alone, fresh caches, canonical paths.
cat > "$work/NuGet.Config" <<'XML'
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
consumer="$work/consumer"; mkdir -p "$consumer/templates"
cp "$work/NuGet.Config" "$consumer/templates/NuGet.Config"
export NUGET_HTTP_CACHE_PATH="$consumer/http-cache" NUGET_PLUGINS_CACHE_PATH="$consumer/plugin-cache"
{
  echo '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>'
  for id in $ids; do
    test "$id" = PouryaKit.Templates || echo "<PackageReference Include=\"$id\" Version=\"[$version]\" />"
  done
  echo '</ItemGroup></Project>'
} > "$consumer/Consumer.csproj"
dotnet restore "$consumer/Consumer.csproj" --packages "$consumer/packages" --configfile "$work/NuGet.Config"
proven="37 library packages restored at [$version]"
(cd "$consumer/templates" && dotnet new install "PouryaKit.Templates@$version" --debug:custom-hive "$consumer/hive")
proven="$proven; PouryaKit.Templates@$version installed"
for template in $(jq -er '.shortName' "$work/cmp/PouryaKit.Templates/original/content/"*/.template.config/template.json); do
  for name in Acme.Shop acme-shop; do
    (cd "$consumer/templates" && dotnet new "$template" -n "$name" -o "$template/$name" --no-restore --debug:custom-hive "$consumer/hive")
    dotnet restore "$consumer/templates/$template/$name/$name.slnx" --packages "$consumer/packages" --configfile "$work/NuGet.Config"
    proven="$proven; $template -n $name generated and restored"
  done
done
rm -rf "${work:?}"
finished=yes
echo "PASS nuget.org publication of $version from $expected_commit: $pushed pushed; from nuget.org alone with fresh caches: $proven."
echo "Log: $log. Revoke the API key if you have not."
