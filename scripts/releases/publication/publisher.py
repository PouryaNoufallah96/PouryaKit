"""One private publisher for preserved original packages, shared by main and named cuts."""

import hashlib
import io
import json
from pathlib import Path
import re
import sys
import time
import urllib.parse
import uuid
import xml.etree.ElementTree as ET
import zipfile

from artifacts import read_json, sha
from transport import API, FEED, MAX_BYTES, OWNER, REPOSITORY, PublicationError

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts/releases/preflight"))
from preflight import parse_version


def package_identity(content, package_id, version, commit):
    try:
        with zipfile.ZipFile(io.BytesIO(content)) as archive:
            specs = [entry for entry in archive.infolist() if entry.filename.endswith(".nuspec")]
            if len(specs) != 1 or specs[0].file_size > 1024 * 1024:
                raise PublicationError("Package must contain exactly one bounded nuspec.")
            metadata = ET.fromstring(archive.read(specs[0]))
    except (zipfile.BadZipFile, ET.ParseError):
        raise PublicationError("Package archive or nuspec is invalid.") from None
    # Nuspec schemas use a namespace; compare its local names without accepting multiple metadata nodes.
    for element in metadata.iter():
        element.tag = element.tag.split("}")[-1]
    nodes = metadata.findall("metadata")
    if len(nodes) != 1:
        raise PublicationError("Package nuspec metadata is ambiguous.")
    node = nodes[0]
    repositories = node.findall("repository")
    if (len(node.findall("id")) != 1 or node.findtext("id") != package_id
            or len(node.findall("version")) != 1 or node.findtext("version") != version
            or len(repositories) != 1 or repositories[0].get("commit") != commit
            or repositories[0].get("url") != f"https://github.com/{REPOSITORY}"):
        raise PublicationError("Package ID, version, repository or source SHA differs from the preserved manifest.")


def load_candidate(directory, expected_sha):
    expected_sha = sha(expected_sha)
    manifest = read_json(directory / "manifest.json")
    if manifest.get("commit") != expected_sha:
        raise PublicationError("Manifest differs from the externally verified source SHA.")
    version = manifest.get("version", "")
    try:
        parsed = parse_version(version)
    except (ValueError, TypeError):
        raise PublicationError("Preserved package version must be strict SemVer.") from None
    if parsed["major"] != "0" or parsed["channel"] not in ("preview", "rc"):
        raise PublicationError("Private publication accepts only preserved 0.x preview or rc prereleases.")
    if parsed["metadata"]:
        raise PublicationError("Preserved PackageVersion excludes build metadata, as MinVer does.")
    inventory = read_json(ROOT / "scripts/packaging/inventory.json")["packages"]
    expected = {item["id"] for item in inventory}
    packages = manifest.get("packages", [])
    if len(expected) != 13 or len(packages) != len(expected) or {item.get("id") for item in packages} != expected:
        raise PublicationError("Manifest must identify the exact thirteen-package inventory.")
    filenames, originals = set(), {}
    for package in packages:
        filename = package.get("file", "")
        if (filename != f"{package['id']}.{version}.nupkg" or Path(filename).name != filename
                or filename in filenames or not re.fullmatch(r"[0-9a-f]{64}", package.get("sha256", ""))):
            raise PublicationError("Manifest package paths or hashes are unsafe or mismatched.")
        filenames.add(filename)
        path = directory / "packages" / filename
        if (path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_BYTES
                or not path.resolve().is_relative_to(directory.resolve())):
            raise PublicationError("Original package artifact is missing or unsafe.")
        content = path.read_bytes()
        if hashlib.sha256(content).hexdigest() != package["sha256"]:
            raise PublicationError("Original package bytes differ from the preserved hash.")
        package_identity(content, package["id"], version, expected_sha)
        originals[package["id"]] = content
    if {path.name for path in (directory / "packages").glob("*.nupkg")} != filenames:
        raise PublicationError("Original package directory differs from the manifest inventory.")
    return manifest, originals


class Feed:
    def __init__(self, client):
        self.client = client
        index, _ = client.document(FEED, auth="feed")
        resources = index.get("resources", [])
        self.base = self.resource(resources, "PackageBaseAddress/3.0.0")
        self.push = self.resource(resources, "PackagePublish/2.0.0")
        client.checked_url(self.base, "feed")
        client.checked_url(self.push, "feed")
        # A successful authenticated enumeration is required before interpreting content 404 as absence.
        self.metadata = {}
        for package in client.pages(f"{API}/users/{OWNER}/packages?package_type=nuget&per_page=100"):
            name = package.get("name")
            if not isinstance(name, str) or package.get("package_type") != "nuget":
                raise PublicationError("Private package enumeration has an unsupported shape.")
            if name.lower() in self.metadata:
                raise PublicationError("Private package enumeration contains duplicate identities.")
            self.metadata[name.lower()] = package

    @staticmethod
    def resource(resources, kind):
        matches = [item.get("@id") for item in resources if item.get("@type") == kind]
        if len(matches) != 1 or not isinstance(matches[0], str):
            raise PublicationError("Private NuGet index lacks one unambiguous required resource.")
        return matches[0]

    def versions(self, package_id):
        document, _ = self.client.document(f"{self.base.rstrip('/')}/{package_id.lower()}/index.json", auth="feed", missing=True)
        if document is None:
            if package_id.lower() in self.metadata:
                raise PublicationError("Known private package returned an unavailable version index.")
            return []
        versions = document.get("versions")
        if not isinstance(versions, list) or any(not isinstance(version, str) for version in versions):
            raise PublicationError("Private NuGet version index has an unsupported shape.")
        if len(versions) != len(set(versions)):
            raise PublicationError("Private NuGet version index contains duplicate identities.")
        return versions

    def private(self, package_id, *, polling=False):
        metadata, _ = self.client.document(f"{API}/users/{OWNER}/packages/nuget/{urllib.parse.quote(package_id.lower(), safe='')}", missing=polling)
        if metadata is None:
            return None
        if (metadata.get("name", "").lower() != package_id.lower() or metadata.get("package_type") != "nuget"
                or metadata.get("visibility") != "private"
                or metadata.get("repository", {}).get("full_name") != REPOSITORY):
            raise PublicationError("Remote package is not observed private and linked to the fixed repository.")
        return {"visibility": "private", "repository": REPOSITORY}

    def content(self, package_id, version):
        package_id, version = package_id.lower(), version.lower()
        url = f"{self.base.rstrip('/')}/{package_id}/{version}/{package_id}.{version}.nupkg"
        status, content, _ = self.client.request("GET", url, auth="feed", missing=True)
        return None if status == 404 else content

    def upload(self, package, content):
        boundary = "pouryakit-" + uuid.uuid4().hex
        body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"package\"; filename=\"{package['file']}\"\r\n"
                "Content-Type: application/octet-stream\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
        status, _, _ = self.client.request("PUT", self.push, auth="feed", data=body,
                                         headers={"Content-Type": f"multipart/form-data; boundary={boundary}",
                                                  "X-NuGet-ApiKey": self.client.token})
        if status not in (201, 202, 409):
            raise PublicationError("Private feed did not accept the upload.")
        return status


def verify_remote(feed, package, manifest, *, polling=False):
    for attempt in range(10 if polling else 1):
        content = feed.content(package["id"], manifest["version"])
        if content is not None:
            package_identity(content, package["id"], manifest["version"], manifest["commit"])
            if hashlib.sha256(content).hexdigest() != package["sha256"]:
                raise PublicationError("Remote package bytes conflict with the original hash.")
            metadata = feed.private(package["id"], polling=polling)
            if metadata is not None:
                return {"id": package["id"], "version": manifest["version"], "sha256": package["sha256"],
                        "source_sha": manifest["commit"], **metadata}
        if polling:
            time.sleep(6)
    raise PublicationError("Expected remote package bytes are unavailable.")


def publish(directory, expected_sha, client, receipt, save, *, fail_after_one=False, probe=False):
    manifest, originals = load_candidate(directory, expected_sha)
    receipt.update({"source_sha": manifest["commit"], "version": manifest["version"],
                    "expected": manifest["packages"], "uploaded": [], "confirmed": [], "verified": []})
    save()
    feed = Feed(client)
    receipt["protocol"] = {"index": FEED, "package_base_address": feed.base, "package_publish": feed.push,
                           "metadata_route": f"{API}/users/{OWNER}/packages?package_type=nuget&per_page=100",
                           "enumerated_count": len(feed.metadata), "enumerated_expected": {
                               package["id"]: {key: feed.metadata[package["id"].lower()].get(key)
                                               for key in ("id", "name", "visibility", "package_type")}
                               for package in manifest["packages"] if package["id"].lower() in feed.metadata}}
    receipt["versions_before"] = {}
    missing = []
    # Complete the read/check pass before the first PUT, even when the conflict is the last ID.
    for package in manifest["packages"]:
        versions = feed.versions(package["id"])
        if len(versions) > 10000:
            raise PublicationError("Private version history exceeds the receipt limit.")
        receipt["versions_before"][package["id"]] = versions
        if manifest["version"].lower() in [version.lower() for version in versions]:
            receipt["verified"].append(verify_remote(feed, package, manifest))
        else:
            if feed.content(package["id"], manifest["version"]) is not None:
                raise PublicationError("Private feed content and version index disagree.")
            if package["id"].lower() in feed.metadata:
                feed.private(package["id"])
            missing.append(package)
    receipt["missing_before"] = [package["id"] for package in missing]
    save()
    if probe:
        receipt["mode"] = "read-only-probe"
        save()
        return
    for package in missing:
        status = feed.upload(package, originals[package["id"]])
        if status != 409:
            receipt["uploaded"].append(package["id"])
        save()
        verified = verify_remote(feed, package, manifest, polling=True)
        receipt["confirmed"].append(verified)
        save()
        if fail_after_one and status != 409:
            raise PublicationError("Hosted proof stopped after one confirmed original upload.")
    # Success proves every original byte hash and private metadata again after all uploads.
    receipt["verified"] = [verify_remote(feed, package, manifest, polling=True) for package in manifest["packages"]]
    receipt["complete"] = len(receipt["verified"]) == 13
    save()
