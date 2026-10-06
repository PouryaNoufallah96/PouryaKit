"""Recover one immutable successful trusted-main validation artifact, never rebuild it."""

from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile

from transport import API, MAX_BYTES, REPOSITORY, PublicationError


def positive(value):
    if not re.fullmatch(r"[1-9][0-9]*", str(value)):
        raise PublicationError("Run, attempt and artifact IDs must be positive integers.")
    return str(value)


def sha(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise PublicationError("An exact forty-character source SHA is required.")
    return value


def read_json(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_BYTES:
        raise PublicationError("Original validation evidence is missing or unsafe.")
    try:
        return json.loads(path.read_bytes())
    except (ValueError, UnicodeError):
        raise PublicationError("Original validation evidence is invalid JSON.") from None


def producer(client, run_id, attempt, expected_sha):
    run_id, attempt = positive(run_id), positive(attempt)
    expected_sha = sha(expected_sha)
    prefix = f"{API}/repos/{REPOSITORY}"
    repository, _ = client.document(prefix)
    if repository.get("full_name") != REPOSITORY or repository.get("private") is not True or repository.get("default_branch") != "main":
        raise PublicationError("Publication requires the fixed private repository and main default branch.")
    run, _ = client.document(f"{prefix}/actions/runs/{run_id}/attempts/{attempt}")
    if (str(run.get("id")) != run_id or str(run.get("run_attempt")) != attempt
            or run.get("event") != "push" or run.get("head_branch") != "main"
            or run.get("head_sha") != expected_sha or run.get("path") != ".github/workflows/ci.yml"
            or run.get("repository", {}).get("full_name") != REPOSITORY
            or run.get("head_repository", {}).get("full_name") != REPOSITORY):
        raise PublicationError("Original run is not the selected trusted main CI push and attempt.")
    # Overall run status is deliberately irrelevant: successful jobs may precede an interrupted publisher.
    jobs = client.pages(f"{prefix}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100", "jobs")
    for name in ("ai-setup", "build-and-test / validate", "publication-protocol"):
        matches = [job for job in jobs if job.get("name") == name]
        if len(matches) != 1 or matches[0].get("status") != "completed" or matches[0].get("conclusion") != "success":
            raise PublicationError("Original AI, package validation and publication protocol jobs must all have succeeded.")
    return run_id, attempt, expected_sha, prefix


def restore(client, run_id, attempt, artifact_id, expected_sha, destination, receipt):
    run_id, attempt, expected_sha, prefix = producer(client, run_id, attempt, expected_sha)
    name = f"package-validation-{run_id}-{attempt}"
    artifacts = client.pages(f"{prefix}/actions/runs/{run_id}/artifacts?per_page=100", "artifacts")
    matches = [item for item in artifacts if item.get("name") == name]
    if len(matches) != 1:
        raise PublicationError("The original run-attempt artifact is missing or ambiguous.")
    selected = matches[0]
    artifact_id = positive(artifact_id or selected["id"])
    if str(selected["id"]) != artifact_id:
        raise PublicationError("Artifact ID does not identify the original validation attempt.")
    artifact, _ = client.document(f"{prefix}/actions/artifacts/{artifact_id}")
    if artifact != selected:
        raise PublicationError("Artifact metadata changed during recovery.")
    workflow_run = artifact.get("workflow_run", {})
    if (str(workflow_run.get("id")) != run_id or workflow_run.get("head_sha") != expected_sha
            or workflow_run.get("head_branch") != "main" or artifact.get("expired") is not False):
        raise PublicationError("Original artifact provenance is mismatched or expired.")
    try:
        expiration = datetime.fromisoformat(artifact["expires_at"].replace("Z", "+00:00"))
        if expiration <= datetime.now(timezone.utc):
            raise ValueError()
    except (KeyError, ValueError, TypeError):
        raise PublicationError("Original artifact expiration is unavailable or expired.") from None
    digest = artifact.get("digest", "")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise PublicationError("Original artifact archive digest is unavailable.")
    receipt["original"] = {"repository": REPOSITORY, "run_id": run_id, "run_attempt": attempt,
                           "artifact_id": artifact_id, "artifact_name": name, "digest": digest,
                           "expires_at": artifact["expires_at"], "source_sha": expected_sha}
    _, archive, _ = client.request("GET", f"{prefix}/actions/artifacts/{artifact_id}/zip")
    if "sha256:" + hashlib.sha256(archive).hexdigest() != digest:
        raise PublicationError("Original artifact archive digest differs from retained metadata.")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            members = bundle.infolist()
            if sum(item.file_size for item in members) > MAX_BYTES or len(members) > 1000:
                raise PublicationError("Original artifact exceeds extraction limits.")
            seen = set()
            for item in members:
                path = PurePosixPath(item.filename)
                if (path.is_absolute() or ".." in path.parts or "\\" in item.filename
                        or item.filename in seen or stat.S_ISLNK(item.external_attr >> 16)):
                    raise PublicationError("Original artifact contains an unsafe archive path.")
                seen.add(item.filename)
                target = destination / path
                if item.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(bundle.read(item))
    except zipfile.BadZipFile:
        raise PublicationError("Original artifact is not a valid archive.") from None
    validation = read_json(destination / "validation.json")
    revision = read_json(destination / "revision.json")
    identity = {"repository": REPOSITORY, "run_id": run_id, "run_attempt": attempt,
                "event": "push", "ref": "refs/heads/main", "tested_sha": expected_sha,
                "pull_request": None, "shallow": False}
    if any(validation.get(key) != value or revision.get(key) != value for key, value in identity.items()):
        raise PublicationError("Original validation receipt does not match trusted hosted provenance.")
    if (validation.get("validation_result") != "success" or validation.get("manifest") != "manifest.json"
            or validation.get("artifact_name") != name):
        raise PublicationError("Original artifact is not a successful package validation receipt.")
    history = read_json(destination / "history.json")
    manifest = read_json(destination / "manifest.json")
    evidence = read_json(destination / "package-evidence.json")
    if (history.get("shallow") is not False or history.get("mode") != "checkout"
            or history.get("commit") != expected_sha or not isinstance(history.get("tags"), list)
            or history.get("tags") != revision.get("tags") or revision.get("tags") != validation.get("tags")):
        raise PublicationError("Original full-history and tag evidence is mismatched.")
    if (validation.get("version") != manifest.get("version") or evidence.get("Version") != manifest.get("version")
            or evidence.get("Commit") != expected_sha or manifest.get("commit") != expected_sha):
        raise PublicationError("Original MinVer version or source SHA evidence is mismatched.")
    return destination
