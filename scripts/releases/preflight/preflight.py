#!/usr/bin/env python3
"""Read-only named-release policy over preserved packages, Git history and SDK evidence."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from urllib.parse import quote
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[3]
SDK = Path(__file__).parent / "sdk"
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
                    r"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
                    r"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?"
                    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def run(command, *, cwd=ROOT, log=None, allow_failure=False, stdin=None):
    # Ambient Git trace consumers may write notes even during clone/read operations.
    env = {**os.environ, "GIT_TRACE2_EVENT": "0"}
    result = subprocess.run(list(map(str, command)), cwd=cwd, input=stdin, env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if log:
        Path(log).write_text(result.stdout)
    if result.returncode and not allow_failure:
        raise ValueError(f"Command failed ({result.returncode}): {' '.join(map(str, command))}\n{result.stdout}")
    return result


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text())


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n")


def snapshot(repo):
    return {name: run(["git", "-C", repo, *args]).stdout for name, args in {
        "refs": ["show-ref"], "head": ["rev-parse", "HEAD"],
        "status": ["status", "--porcelain=v1", "--untracked-files=all"]}.items()}


def inventory_ids(path):
    ids = [item["id"] for item in read_json(path)["packages"]]
    if not ids or len(set(ids)) != len(ids):
        raise ValueError("Inventory must be nonempty and unique.")
    return ids


def packages(folder, expected, output):
    folder = Path(folder).resolve()
    manifest_path = folder / "manifest.json"
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    (output / "manifest.json").write_bytes(manifest_bytes)
    if not re.fullmatch(r"[0-9a-f]{40}", manifest["commit"]):
        raise ValueError("Manifest requires the full source commit SHA.")
    rows = manifest["packages"]
    if len(rows) != len(expected) or {row["id"] for row in rows} != set(expected):
        raise ValueError("Manifest package inventory differs from the expected set.")
    filenames = {row["file"] for row in rows}
    if len(filenames) != len(rows) or filenames != {file.name for file in (folder / "packages").glob("*.nupkg")}:
        raise ValueError("Package files differ from the manifest.")
    paths = {}
    references = {}
    for row in sorted(rows, key=lambda item: item["id"]):
        if Path(row["file"]).name != row["file"] or not row["file"].endswith(".nupkg"):
            raise ValueError("Manifest package filenames must be direct .nupkg children.")
        package = folder / "packages" / row["file"]
        if digest(package) != row["sha256"]:
            raise ValueError(f"Package checksum differs: {row['id']}")
        # Compare frozen bytes, so concurrent replacement cannot change the validated input.
        frozen = output / row["file"]
        shutil.copyfile(package, frozen)
        if digest(frozen) != row["sha256"]:
            raise ValueError(f"Package changed while freezing: {row['id']}")
        paths[row["id"]] = frozen
        with zipfile.ZipFile(frozen) as archive:
            nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise ValueError(f"Expected one nuspec: {row['id']}")
            metadata = ET.fromstring(archive.read(nuspecs[0]))
            ns = {"n": metadata.tag.partition("}")[0].lstrip("{")}
            identity = metadata.find("n:metadata", ns)
            if identity.findtext("n:id", namespaces=ns) != row["id"] or identity.findtext("n:version", namespaces=ns) != manifest["version"]:
                raise ValueError(f"Package identity differs from the manifest: {row['id']}")
            repository = identity.find("n:repository", ns)
            if repository is None or repository.get("commit") != manifest["commit"]:
                raise ValueError(f"Package source commit differs: {row['id']}")
            for name in archive.namelist():
                parts = name.split("/")
                if len(parts) == 3 and parts[0] == "lib" and parts[2].endswith(".dll"):
                    target = output / "references" / parts[1] / parts[2]
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.read(name))
                    references.setdefault(parts[1], []).append(target)
    return {"commit": manifest["commit"], "version": manifest["version"],
            "manifest_sha256": hashlib.sha256(manifest_bytes).hexdigest(), "packages": rows}, paths, references


def github_history(repo, expected, owner_kind, evidence):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo):
        raise ValueError("Expected owner/repository.")
    owner = repo.split("/")[0]

    def get(endpoint, name):
        result = run(["gh", "api", "--method", "GET", "--paginate", "--slurp", endpoint],
                     log=evidence / f"{name}.json")
        pages = json.loads(result.stdout)
        if not isinstance(pages, list) or not all(isinstance(page, list) for page in pages):
            raise ValueError("GitHub history did not return complete list pages.")
        return [row for page in pages for row in page]

    tags = get(f"/repos/{repo}/tags?per_page=100", "named-tags")
    named = [{"version": tag["name"][1:], "commit": tag["commit"]["sha"]}
             for tag in tags if re.match(r"^v[0-9]+\.", tag["name"])]
    # Enumerate packages first: a failed/unauthorized version lookup must never mean empty history.
    visible = get(f"/{owner_kind}/{owner}/packages?package_type=nuget&per_page=100", "visible-packages")
    published = []
    for package in visible:
        matching = next((name for name in expected if name.lower() == package["name"].lower()), None)
        if matching:
            versions = get(f"/{owner_kind}/{owner}/packages/nuget/{quote(package['name'], safe='')}/versions?per_page=100",
                           matching)
            published.extend({"id": matching, "version": version["name"]} for version in versions)
    return {"source": "github-observed", "repository": repo,
            "observed_at": datetime.now(timezone.utc).isoformat(), "named": named, "published": published}


def parse_version(value):
    match = SEMVER.fullmatch(value)
    if not match:
        raise ValueError(f"Invalid SemVer: {value}")
    release = match[4].split(".") if match[4] else []
    return {"major": match[1], "minor": match[2], "patch": match[3],
            "release": release, "channel": release[0] if release else None, "metadata": match[5] or ""}


def compare_number(left, right):
    # Arbitrarily large SemVer numbers need neither Int32 nor Python integer conversion.
    return ((len(left), left) > (len(right), right)) - ((len(left), left) < (len(right), right))


def compare_versions(left, right):
    for name in ("major", "minor", "patch"):
        difference = compare_number(left[name], right[name])
        if difference:
            return difference
    left_release, right_release = left["release"], right["release"]
    if not left_release or not right_release:
        return (not left_release) - (not right_release)
    for left_field, right_field in zip(left_release, right_release):
        if left_field.isdigit() and right_field.isdigit():
            difference = compare_number(left_field, right_field)
        elif left_field.isdigit() != right_field.isdigit():
            difference = -1 if left_field.isdigit() else 1
        else:
            difference = (left_field > right_field) - (left_field < right_field)
        if difference:
            return difference
    return (len(left_release) > len(right_release)) - (len(left_release) < len(right_release))


def progression(requested, history, work):
    named = history["named"]
    published = history["published"]
    if len({row["version"] for row in named}) != len(named):
        raise ValueError("Named history contains duplicate versions.")
    for row in named:
        if not re.fullmatch(r"[0-9a-f]{40}", row["commit"]):
            raise ValueError("Named history requires full commit SHAs.")
    candidate = parse_version(requested)
    if candidate["major"] != "0" or candidate["channel"] not in ("preview", "rc"):
        raise ValueError("Named cuts initially require a 0.x preview or rc version.")
    last, baseline_version = None, None
    for row in [*named, *published]:
        prior = parse_version(row["version"])
        if compare_versions(candidate, prior) <= 0:
            raise ValueError("Requested version must exceed every named and published version, ignoring build metadata.")
    if any(requested.split("+")[0].lower() == row["version"].split("+")[0].lower() for row in published):
        raise ValueError("Requested version conflicts with an already published NuGet identity.")
    for row in named:
        prior = parse_version(row["version"])
        if baseline_version is None or compare_versions(prior, baseline_version) > 0:
            last, baseline_version = row, prior
    if not named:
        return None, candidate, None
    return last, candidate, baseline_version


def reference_tool(work):
    project = work / "reference-tool"
    shutil.copytree(SDK, project)
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        (project / name).write_text("<Project />\n")
    shutil.copyfile(ROOT / "global.json", project / "global.json")
    target = project / "ReferenceGuard.csproj"
    run(["dotnet", "build", target, "-c", "Release", "--nologo"], cwd=project, log=work / "reference-tool.log")
    run(["dotnet", "msbuild", target, "-nologo", "-t:ResolveReferences", "-getItem:ReferencePath"],
        cwd=project, log=work / "references.json")
    framework_refs = [Path(item["Identity"]) for item in read_json(work / "references.json")["Items"]["ReferencePath"]
                      if item.get("NuGetPackageId") in ("Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref")]
    if not framework_refs:
        raise ValueError("Pinned SDK framework references were not resolved.")
    return project / "bin/Release/net10.0/ReferenceGuard.dll", framework_refs


def minver(repo, commit, requested, work, project):
    if run(["git", "-C", repo, "rev-parse", "--is-shallow-repository"]).stdout.strip() != "false":
        raise ValueError("MinVer requires full Git history and tags.")
    scratch = work / "minver"
    run(["git", "clone", "--no-hardlinks", "--no-checkout", repo, scratch], log=work / "clone.log")
    run(["git", "-C", scratch, "checkout", "--detach", commit], log=work / "checkout.log")
    tag = "v" + requested
    existing = run(["git", "-C", scratch, "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}"], allow_failure=True)
    if existing.returncode == 0:
        if existing.stdout.strip() != commit:
            raise ValueError("Requested candidate tag already points at a different commit.")
    else:
        run(["git", "-C", scratch, "tag", tag, commit])
    target = (scratch / project).resolve()
    if not target.is_relative_to(scratch.resolve()) or not target.is_file():
        raise ValueError("MinVer project must belong to the candidate checkout.")
    run(["dotnet", "restore", target], cwd=scratch, log=work / "minver-restore.log")
    result = run(["dotnet", "msbuild", target, "-nologo", "-t:MinVer",
                  "-getProperty:MinVerVersion,PackageVersion,Version,AssemblyVersion,FileVersion"],
                 cwd=scratch, log=work / "minver-properties.log")
    properties = json.loads(result.stdout[result.stdout.index("{"):])["Properties"]
    if any(properties[name] != requested for name in ("MinVerVersion", "PackageVersion", "Version")):
        raise ValueError(f"Requested tag/package/build version does not equal the real MinVer result: {properties}")
    return {"tag": tag, "commit": commit, "properties": properties, "scope": "scratch clone only"}


def compare(candidate, baseline, references, folder, guard, baseline_references=None):
    folder.mkdir(parents=True)
    if not references or any(not files for files in references.values()):
        raise ValueError("SDK validation could not resolve/run: explicit assembly references are required.")
    suppressions = folder / "findings.xml"
    inputs = ET.Element("Project")
    props = ET.SubElement(inputs, "PropertyGroup")
    for name, value in {"Candidate": candidate, "Baseline": baseline or "", "Suppression": suppressions}.items():
        ET.SubElement(props, name).text = str(value)
    items = ET.SubElement(inputs, "ItemGroup")
    for tfm, files in sorted(references.items()):
        if tfm != "net10.0":
            raise ValueError(f"No configured reference pack for {tfm}; comparison cannot prove compatibility.")
        for name, paths in {"References": files, "CandidateReferences": files,
                            "BaselineReferences": (baseline_references or references).get(tfm, [])}.items():
            item = ET.SubElement(items, name, Include=tfm)
            ET.SubElement(item, "TargetFramework").text = tfm
            ET.SubElement(item, "TargetFrameworkMoniker").text = ".NETCoreApp,Version=v10.0"
            ET.SubElement(item, "ReferencePath").text = ",".join(map(str, paths))
    input_file = folder / "inputs.props"
    ET.ElementTree(inputs).write(input_file, encoding="unicode")
    available = run(["dotnet", guard, input_file], log=folder / "reference-guard.log", allow_failure=True)
    if available.returncode:
        raise ValueError(f"SDK validation could not resolve/run: {available.stdout}")
    command = ["dotnet", "msbuild", SDK / "Compare.proj", "-nologo", "-t:Compare", f"-p:Inputs={input_file}"]
    raw = run(command, log=folder / "raw.log", allow_failure=True)
    generated = run([*command, "-p:Generate=true"], log=folder / "generated.log", allow_failure=True)
    # Discovery must succeed. Missing references or tool failures are never reviewable API changes.
    if generated.returncode:
        raise ValueError(f"SDK validation could not resolve/run: {raw.stdout}\n{generated.stdout}")
    findings = []
    if suppressions.exists():
        for element in ET.parse(suppressions).getroot().findall("Suppression"):
            finding = {child.tag: child.text or "" for child in element}
            findings.append(finding)
    if raw.returncode and not findings:
        raise ValueError(f"SDK failed without dispositionable findings: {raw.stdout}")
    return findings, {"exit_code": raw.returncode, "raw_log": str(folder / "raw.log"),
                      "suppression_file": str(suppressions) if suppressions.exists() else None}


def finding_record(package_id, direction, detail, candidate, baseline):
    identity = {"package": package_id, "direction": direction, "diagnostic": detail,
                "policy": "compatible-addition" if direction == "reverse" and detail.get("DiagnosticId") in ("CP0001", "CP0002") else "reviewed-break",
                "candidate": candidate, "baseline": baseline}
    identity["key"] = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return identity


def validate_review(review):
    if not isinstance(review, dict):
        raise ValueError("Review must be a JSON object.")
    if "candidate" in review and not isinstance(review["candidate"], dict):
        raise ValueError("Review candidate must be a JSON object.")
    if "baseline" in review and review["baseline"] is not None and not isinstance(review["baseline"], dict):
        raise ValueError("Review baseline must be a JSON object or null.")
    if "migration_notes" in review and not isinstance(review["migration_notes"], str):
        raise ValueError("Review migration_notes must be a string.")
    rows = review.get("dispositions", [])
    if not isinstance(rows, list):
        raise ValueError("Review dispositions must be a JSON array.")
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            raise ValueError(f"Review dispositions[{index}] must be a JSON object.")
        for field in ("key", "decision", "reviewer", "rationale"):
            if not isinstance(row.get(field), str):
                raise ValueError(f"Review dispositions[{index}].{field} must be a string.")
    return review


def dispositions(findings, review, candidate, baseline, candidate_version, baseline_version):
    rows = [] if review is None else review.get("dispositions", [])
    indexed = {row["key"]: row for row in rows}
    if len(indexed) != len(rows) or set(indexed) != {finding["key"] for finding in findings}:
        raise ValueError("Every actual SDK finding needs one exact disposition; missing, extra or stale dispositions fail.")
    if not findings:
        return
    if review.get("candidate") != candidate or review.get("baseline") != baseline:
        raise ValueError("Review identities differ from the exact candidate/baseline manifests and hashes.")
    if compare_number(candidate_version["minor"], baseline_version["minor"]) <= 0:
        raise ValueError("Additions and pre-1.0 reviewed breaks require a minor increment.")
    for finding in findings:
        row = indexed[finding["key"]]
        expected = finding["policy"]
        if row.get("decision") != expected or not row.get("reviewer", "").strip() or not row.get("rationale", "").strip():
            raise ValueError(f"Finding requires {expected}, a reviewer and rationale: {finding['key']}")
        if expected == "reviewed-break" and not review.get("migration_notes", "").strip():
            raise ValueError("Reviewed pre-1.0 breaks require migration notes.")
        finding["disposition"] = row


def execute(args, output, result):
    expected = inventory_ids(args.inventory)
    sdk_version = run(["dotnet", "--version"]).stdout.strip()
    if sdk_version != read_json(ROOT / "global.json")["sdk"]["version"]:
        raise ValueError("Preflight requires the exact pinned SDK.")
    result["sdk"] = sdk_version
    frozen = output / "candidate"
    frozen.mkdir()
    candidate, candidate_paths, candidate_refs = packages(args.candidate, expected, frozen)
    result["candidate"] = candidate
    if candidate["version"] != args.version:
        raise ValueError("Candidate manifest version does not equal the requested version.")
    observed = args.history is None
    history = (github_history(args.github_repository, expected, args.owner_kind, output)
               if observed else {**read_json(args.history), "source": "fixture"})
    result["history"] = history
    result["history_sha256"] = hashlib.sha256(json.dumps(history, sort_keys=True).encode()).hexdigest()
    last, candidate_version, baseline_version = progression(args.version, history, output)
    baseline = None
    baseline_paths = {}
    if last:
        if not args.baseline:
            raise ValueError("Named history exists; preserved baseline artifacts are required.")
        baseline_folder = output / "baseline"
        baseline_folder.mkdir()
        baseline, baseline_paths, baseline_refs = packages(args.baseline, expected, baseline_folder)
        if baseline["commit"] != last["commit"] or baseline["version"] != last["version"]:
            raise ValueError("Baseline artifacts do not identify the last named release.")
    elif args.baseline:
        raise ValueError("Empty named history cannot have a baseline.")
    result["baseline"] = baseline
    result["baseline_status"] = ("compared" if last else
                                 "no baseline: observed named history is empty" if observed else
                                 "fixture only: empty named history, no hosted baseline claim")
    result["minver"] = minver(args.repo, candidate["commit"], args.version, output, args.minver_project)
    guard, framework_refs = reference_tool(output)
    refs = {tfm: [*framework_refs, *files] for tfm, files in candidate_refs.items()}
    old_refs = {tfm: [*framework_refs, *files] for tfm, files in baseline_refs.items()} if baseline else refs
    for folder in args.reference_directory:
        root = Path(folder)
        if not root.is_dir() or not list(root.rglob("*.dll")):
            raise ValueError(f"Reference directory is absent or empty: {folder}")
        for files in [*refs.values(), *old_refs.values()]:
            files.extend(sorted(root.rglob("*.dll")))
    findings = result["findings"]
    result["comparisons"] = []
    for package_id in expected:
        for direction in (["forward", "reverse"] if baseline else ["intrinsic"]):
            target = candidate_paths[package_id] if direction != "reverse" else baseline_paths[package_id]
            against = baseline_paths.get(package_id) if direction != "reverse" else candidate_paths[package_id]
            current_refs, against_refs = (old_refs, refs) if direction == "reverse" else (refs, old_refs)
            details, evidence = compare(target, against, current_refs, output / "sdk" / package_id / direction,
                                        guard, against_refs)
            for detail in details:
                if direction == "intrinsic" or detail.get("IsBaselineSuppression") != "true":
                    raise ValueError(f"Package itself fails SDK validation: {detail}")
                findings.append(finding_record(package_id, direction, detail, candidate, baseline))
            result["comparisons"].append({"package": package_id, "direction": direction, **evidence})
    review = validate_review(read_json(args.review)) if args.review else None
    dispositions(findings, review, candidate, baseline, candidate_version, baseline_version)
    result["review"] = review
    result["status"] = "passed"
    result["publication_eligible"] = observed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, help="#25 output directory with packages/ and manifest.json")
    parser.add_argument("--version", required=True)
    parser.add_argument("--baseline", help="Preserved last named-release #25 output")
    parser.add_argument("--review", help="Exact per-finding reviewed dispositions JSON")
    parser.add_argument("--history", help="Local history fixture JSON; always ineligible as production evidence")
    parser.add_argument("--github-repository", default="PouryaNoufallah96/PouryaKit")
    parser.add_argument("--owner-kind", choices=["users", "orgs"], default="users")
    parser.add_argument("--inventory", type=Path, default=ROOT / "scripts/packaging/inventory.json")
    parser.add_argument("--repo", type=Path, default=ROOT)
    parser.add_argument("--minver-project", default="src/PouryaKit.Domain/PouryaKit.Domain.csproj")
    parser.add_argument("--reference-directory", action="append", default=[], help="Preserved dependency DLL reference directory")
    parser.add_argument("--output", type=Path, help="Fresh external evidence directory")
    args = parser.parse_args()
    output = args.output.resolve() if args.output else Path(tempfile.mkdtemp(prefix="pouryakit-preflight-"))
    if output.is_relative_to(args.repo.resolve()) or output.is_relative_to(ROOT):
        parser.error("Evidence must be outside the source checkout.")
    if args.output:
        output.mkdir(parents=True, exist_ok=False)
    before = snapshot(args.repo)
    write_json(output / "production-before.json", before)
    result = {"schema": 1, "status": "failed", "publication_eligible": False, "findings": [],
              "limitations": "SDK packaged API/framework checks do not prove semantic compatibility; green-main and owner approval are separate #29 gates."}
    try:
        execute(args, output, result)
    except (ValueError, KeyError, OSError, ET.ParseError, zipfile.BadZipFile) as error:
        result["error"] = str(error)
    finally:
        after = snapshot(args.repo)
        write_json(output / "production-after.json", after)
        if after != before:
            result.update(status="failed", publication_eligible=False, error="Production refs/status changed during preflight.")
        result["production_unchanged"] = after == before
        write_json(output / "result.json", result)
    print(output / "result.json")
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
