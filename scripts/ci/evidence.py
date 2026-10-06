#!/usr/bin/env python3
"""Record checkout identity and retain package evidence without consumer caches."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def git(*arguments):
    return subprocess.check_output(["git", "-C", str(ROOT), *arguments], text=True).strip()


def revision(output):
    commit = git("rev-parse", "HEAD")
    if commit != os.environ["GITHUB_SHA"]:
        raise ValueError("Checkout SHA differs from the workflow's intended revision.")
    if git("rev-parse", "--is-shallow-repository") != "false":
        raise ValueError("Package validation requires full history.")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    pr = event.get("pull_request")
    document = {
        "repository": os.environ["GITHUB_REPOSITORY"],
        "event": os.environ["GITHUB_EVENT_NAME"],
        "ref": os.environ["GITHUB_REF"],
        "tested_sha": commit,
        "run_id": os.environ["GITHUB_RUN_ID"],
        "run_attempt": os.environ["GITHUB_RUN_ATTEMPT"],
        "shallow": False,
        "tags": git("tag", "--list").splitlines(),
        "pull_request": None if not pr else {
            "number": event["number"],
            "head_sha": pr["head"]["sha"],
            "base_sha": pr["base"]["sha"],
            "head_repository": pr["head"]["repo"]["full_name"],
        },
    }
    (output / "revision.json").write_text(json.dumps(document, indent=2) + "\n")
    print(f"Tested checkout: {commit}; event: {document['event']}; ref: {document['ref']}")


def copy_file(source, destination, output):
    if source.is_symlink() or not source.resolve().is_relative_to(output.resolve()):
        raise ValueError(f"Evidence file escapes its directory: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)


def collect(output, result):
    identity = json.loads((output / "revision.json").read_text())
    manifest = None
    if result == "success":
        manifest = json.loads((output / "manifest.json").read_text())
        if manifest["commit"] != identity["tested_sha"]:
            raise ValueError("Manifest does not identify the tested checkout.")
        if not manifest["packages"] or not manifest["version"]:
            raise ValueError("Successful validation requires a nonempty package manifest.")
        for package in manifest["packages"]:
            filename = package["file"]
            if Path(filename).name != filename:
                raise ValueError("Manifest package filename must be a basename.")
            package_file = output / "packages" / filename
            if hashlib.sha256(package_file.read_bytes()).hexdigest() != package["sha256"]:
                raise ValueError(f"Package checksum differs from the validated manifest: {filename}")

    artifact = output / "artifact"
    artifact.mkdir()
    # Only shared-path evidence is retained. Never copy caches, CLI homes, configuration or the raw event.
    files = list(output.glob("*.log"))
    files += [output / name for name in ("revision.json", "history.json", "manifest.json", "package-evidence.json",
                                        "evaluated-projects.json", "minver.json") if (output / name).is_file()]
    files += list((output / "packages").glob("*.nupkg"))
    files += list((output / "consumers").glob("**/obj/project.assets.json"))
    for source in files:
        copy_file(source, artifact / source.relative_to(output), output)
    receipt = {**identity, "validation_result": result,
               "version": manifest["version"] if manifest else None,
               "manifest": "manifest.json" if manifest else None,
               "artifact_name": f"package-validation-{identity['run_id']}-{identity['run_attempt']}"}
    (artifact / "validation.json").write_text(json.dumps(receipt, indent=2) + "\n")
    if "GITHUB_OUTPUT" in os.environ:
        with Path(os.environ["GITHUB_OUTPUT"]).open("a") as stream:
            stream.write(f"tested_sha={identity['tested_sha']}\nvalidation_result={result}\n")
            stream.write(f"version={receipt['version'] or ''}\n")
    if "GITHUB_STEP_SUMMARY" in os.environ:
        with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as stream:
            stream.write(f"Package validation: {result}\n\nTested SHA: `{identity['tested_sha']}`\n\n")
            stream.write(f"MinVer package version: `{receipt['version'] or 'unavailable'}`\n\n")
            if identity["pull_request"]:
                stream.write(f"PR head SHA: `{identity['pull_request']['head_sha']}`. The tested SHA is the merge revision.\n\n")
            stream.write(f"Artifact: `{receipt['artifact_name']}`. Publication requires a successful trusted main run.\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("revision", "collect"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--result", choices=("success", "failure", "cancelled", "skipped"))
    arguments = parser.parse_args()
    if arguments.command == "collect" and arguments.result is None:
        parser.error("collect requires --result")
    try:
        if arguments.command == "revision":
            revision(arguments.output)
        else:
            collect(arguments.output, arguments.result)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"FAIL: {error}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
