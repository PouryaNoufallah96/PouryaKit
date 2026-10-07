#!/usr/bin/env python3
"""Build, test and pack once, then prove the explicit Kit set in isolated consumers."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
ARCHITECTURE = ROOT / "tests/PouryaKit.ArchitectureTests/bin/Release/net10.0/PouryaKit.ArchitectureTests.dll"


def run(command, *, cwd=ROOT, env=None, log=None, expect_failure=False):
    print("+ " + " ".join(map(str, command)), flush=True)
    result = subprocess.run(list(map(str, command)), cwd=cwd, env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if log:
        Path(log).write_text(result.stdout)
    if (result.returncode == 0) == expect_failure:
        print(result.stdout, flush=True)
        raise RuntimeError(f"Unexpected exit {result.returncode}: {command}")
    return result.stdout


def properties(project, names, *, cwd=ROOT, target=None):
    command = ["dotnet", "msbuild", project, "-nologo", "-getProperty:" + ",".join(names)]
    if target:
        command.append("-t:" + target)
    output = run(command, cwd=cwd)
    return json.loads(output[output.index("{"):])["Properties"]


def check_inventory(expected, evaluated):
    ids = [package["id"] for package in expected]
    if not ids or len(ids) != len(set(ids)):
        raise ValueError("The package inventory must be nonempty and unique.")
    packable = {path: values["PackageId"] for path, values in evaluated.items()
                if values["IsPackable"].lower() == "true"}
    wanted = {f"src/{name}/{name}.csproj": name for name in ids}
    if packable != wanted:
        raise ValueError(f"Packable projects differ from inventory. Expected {wanted}; evaluated {packable}")
    for path in wanted:
        if evaluated[path]["EnablePackageValidation"].lower() != "true":
            raise ValueError(f"Package validation is disabled in {path}.")


def inventory_controls(expected, evaluated, output):
    controls = {
        "empty": [],
        "missing": expected[:-1],
        "extra": [*expected, {"id": "PouryaKit.Unexpected"}],
    }
    lines = []
    for name, packages in controls.items():
        try:
            check_inventory(packages, evaluated)
        except ValueError as error:
            lines.append(f"PASS {name}: {error}")
        else:
            raise RuntimeError(f"Inventory accepted the {name} control.")
    changed = {path: dict(values) for path, values in evaluated.items()}
    changed[next(path for path in changed if path.startswith("src/"))]["PackageId"] = "PouryaKit.Wrong"
    try:
        check_inventory(expected, changed)
    except ValueError as error:
        lines.append(f"PASS mismatched-id: {error}")
    else:
        raise RuntimeError("Inventory accepted a mismatched package ID.")
    output.write_text("\n".join(lines) + "\n")


def consumer_config(folder, packages):
    (folder / "global.json").write_text((ROOT / "global.json").read_text())
    (folder / "Directory.Build.props").write_text(
        '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>latest</LangVersion>'
        '<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>'
        '<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>\n')
    (folder / "Directory.Build.targets").write_text("<Project />\n")
    (folder / "Directory.Packages.props").write_text((ROOT / "Directory.Packages.props").read_text())
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="produced", value=str(packages))
    ET.SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    produced = ET.SubElement(mapping, "packageSource", key="produced")
    ET.SubElement(produced, "package", pattern="PouryaKit.*")
    public = ET.SubElement(mapping, "packageSource", key="nuget")
    ET.SubElement(public, "package", pattern="*")
    ET.ElementTree(config).write(folder / "NuGet.Config", encoding="unicode")


def isolated_env(folder):
    cache = folder.parent.parent / "caches" / folder.name
    return {**os.environ, "NUGET_PACKAGES": str(cache / "packages"),
            "NUGET_HTTP_CACHE_PATH": str(cache / "http"),
            "NUGET_PLUGINS_CACHE_PATH": str(cache / "plugins"),
            "NUGET_FALLBACK_PACKAGES": "", "DOTNET_CLI_HOME": str(cache / "cli")}


def simple_consumer(folder, ids, types, version):
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties_node = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties_node, "OutputType").text = "Exe"
    packages = ET.SubElement(project, "ItemGroup")
    for name in ids:
        ET.SubElement(packages, "PackageReference", Include=name, Version=version)
    ET.ElementTree(project).write(folder / "Consumer.csproj", encoding="unicode")
    anchors = ", ".join(f"typeof({types[name]})" for name in ids)
    (folder / "Program.cs").write_text(f"System.Type[] types = [{anchors}];\nSystem.Console.WriteLine(types.Length);\n")
    return folder / "Consumer.csproj"


def sample_consumer(folder, source, entries, version):
    shutil.copytree(ROOT / source, folder / "src", ignore=shutil.ignore_patterns("bin", "obj"))
    for project in (folder / "src").rglob("*.csproj"):
        document = ET.parse(project)
        for group in document.getroot().findall("ItemGroup"):
            for reference in list(group.findall("ProjectReference")):
                include = reference.attrib["Include"].replace("\\", "/")
                if "/src/PouryaKit." in include:
                    name = Path(include).stem
                    group.remove(reference)
                    ET.SubElement(group, "PackageReference", Include=name, Version=version)
                else:
                    reference.set("Include", include)
        document.write(project, encoding="unicode")
    return [folder / "src" / entry for entry in entries]


def check_cache_folders(folders, cache):
    if len(folders) != 1 or Path(folders[0]).resolve() != Path(cache).resolve():
        raise RuntimeError(f"Consumer used other package caches: {folders}")


def check_consumer(folder, env, package_ids, version, excluded):
    assets = sorted(folder.glob("**/obj/project.assets.json"))
    if not assets:
        raise RuntimeError("Consumer did not produce restored assets.")
    found = set()
    for asset in assets:
        document = json.loads(asset.read_text())
        folders = list(document["packageFolders"])
        check_cache_folders(folders, env["NUGET_PACKAGES"])
        for name, library in document["libraries"].items():
            name_id, resolved_version = name.split("/")
            if library["type"] == "project":
                resolved = (asset.parent.parent / library["path"]).resolve()
                if not resolved.is_relative_to(folder.resolve()):
                    raise RuntimeError(f"Consumer has a source fallback: {resolved}")
            if not name_id.startswith("PouryaKit."):
                continue
            found.add(name_id)
            if library["type"] != "package" or name_id not in package_ids or resolved_version != version:
                raise RuntimeError(f"Unexpected Kit dependency: {name}")
            cache = Path(env["NUGET_PACKAGES"]) / library["path"]
            metadata = json.loads((cache / ".nupkg.metadata").read_text())
            if Path(metadata["source"]).resolve() != (folder.parent.parent / "packages").resolve():
                raise RuntimeError(f"Kit dependency did not come from produced packages: {metadata}")
            cached = next(cache.glob("*.nupkg"))
            produced = folder.parent.parent / "packages" / f"{name_id}.{version}.nupkg"
            if hashlib.sha256(cached.read_bytes()).digest() != hashlib.sha256(produced.read_bytes()).digest():
                raise RuntimeError(f"Consumer substituted package bytes: {name}")
    if any(any(name.startswith(prefix) for name in found) for prefix in excluded):
        raise RuntimeError(f"Consumer brought excluded dependencies: {found}")
    return list(map(str, assets))


def version_proof(output):
    folder = output / "minver-proof"
    folder.mkdir()
    for file in ("global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        shutil.copy(ROOT / file, folder / file)
    project = folder / "src/VersionProof/VersionProof.csproj"
    project.parent.mkdir(parents=True)
    project.write_text('<Project Sdk="Microsoft.NET.Sdk" />\n')
    run(["git", "-C", folder, "init", "-q"])
    run(["git", "-C", folder, "add", "."])
    run(["git", "-C", folder, "-c", "user.name=Package proof", "-c", "user.email=proof@example.invalid", "commit", "-qm", "proof"])
    run(["dotnet", "restore", project], cwd=folder, log=output / "minver-restore.log")
    root = properties(project, ["MinVerVersion", "PackageVersion"], cwd=folder, target="MinVer")
    if root != {"MinVerVersion": "0.1.0-preview.0", "PackageVersion": "0.1.0-preview.0"}:
        raise RuntimeError(f"Unexpected root MinVer output: {root}")
    run(["git", "-C", folder, "-c", "user.name=Package proof", "-c", "user.email=proof@example.invalid", "commit", "--allow-empty", "-qm", "untagged descendant"])
    untagged = properties(project, ["MinVerVersion", "PackageVersion"], cwd=folder, target="MinVer")
    if untagged != {"MinVerVersion": "0.1.0-preview.0.1", "PackageVersion": "0.1.0-preview.0.1"}:
        raise RuntimeError(f"Unexpected untagged MinVer output: {untagged}")
    run(["git", "-C", folder, "tag", "v0.2.0-rc.1"])
    tagged = properties(project, ["MinVerVersion", "PackageVersion"], cwd=folder, target="MinVer")
    if tagged != {"MinVerVersion": "0.2.0-rc.1", "PackageVersion": "0.2.0-rc.1"}:
        raise RuntimeError(f"Unexpected tagged MinVer output: {tagged}")
    run(["git", "-C", folder, "-c", "user.name=Package proof", "-c", "user.email=proof@example.invalid", "commit", "--allow-empty", "-qm", "after tag"])
    after = properties(project, ["MinVerVersion", "PackageVersion"], cwd=folder, target="MinVer")
    if after != {"MinVerVersion": "0.2.0-rc.1.1", "PackageVersion": "0.2.0-rc.1.1"}:
        raise RuntimeError(f"Unexpected post-tag MinVer output: {after}")
    (output / "minver.json").write_text(json.dumps({"root": root, "untagged": untagged, "tagged": tagged, "after": after}, indent=2))


def artifact_controls(evidence, output):
    original = Path(evidence["Directory"])
    cases = ("missing-package", "extra-package", "wrong-version", "missing-notices", "wrong-commit", "forbidden-dependency", "missing-sourcelink")
    for case in cases:
        destination = output / "controls" / case
        shutil.copytree(original, destination)
        target = destination / f"PouryaKit.Domain.{evidence['Version']}.nupkg"
        if case == "missing-package":
            target.unlink()
        elif case == "extra-package":
            shutil.copy(target, destination / "extra.nupkg")
        else:
            replacement = target.with_suffix(".new")
            with zipfile.ZipFile(target) as source, zipfile.ZipFile(replacement, "w") as changed:
                for entry in source.infolist():
                    content = source.read(entry)
                    if case == "missing-notices" and entry.filename == "THIRD-PARTY-NOTICES.md":
                        continue
                    if case == "missing-sourcelink" and entry.filename.endswith(".dll"):
                        content = b"invalid assembly"
                    if entry.filename.endswith(".nuspec"):
                        document = ET.fromstring(content)
                        if case == "wrong-version":
                            next(node for node in document.iter() if node.tag.endswith("}version")).text = "0.9.0-preview.99"
                        if case == "wrong-commit":
                            next(node for node in document.iter() if node.tag.endswith("}repository")).set("commit", "0" * 40)
                        if case == "forbidden-dependency":
                            dependencies = next(node for node in document.iter() if node.tag.endswith("}dependencies"))
                            group = next(iter(dependencies))
                            namespace = dependencies.tag.split("}")[0] + "}"
                            ET.SubElement(group, namespace + "dependency", id="Microsoft.EntityFrameworkCore", version="10.0.12")
                        content = ET.tostring(document)
                    changed.writestr(entry, content)
            replacement.replace(target)
        control = {**evidence, "Directory": str(destination)}
        path = output / "controls" / f"{case}.json"
        path.write_text(json.dumps(control))
        result = run(["dotnet", ARCHITECTURE, "--validate-packages", path], log=output / f"control-{case}.log", expect_failure=True)
        print(f"PASS negative control {case}: {result.splitlines()[0]}", flush=True)

    # Change a real restored transitive edge, keeping the shipped nuspec and its allowed direct references intact.
    neutral = next(consumer for consumer in evidence["Consumers"] if consumer["Name"] == "PouryaKit.Messaging.Wolverine")
    dapper = next(consumer for consumer in evidence["Consumers"] if consumer["Name"] == "PouryaKit.Persistence.Dapper")
    assets = json.loads(Path(neutral["Assets"][0]).read_text())
    dapper_assets = json.loads(Path(dapper["Assets"][0]).read_text())
    cache = output / "controls/forbidden-transitive/cache"
    shutil.copytree(Path(next(iter(assets["packageFolders"]))), cache)
    npgsql = next(name for name in dapper_assets["libraries"] if name.startswith("Npgsql/"))
    npgsql_library = dapper_assets["libraries"][npgsql]
    shutil.copytree(Path(next(iter(dapper_assets["packageFolders"]))) / npgsql_library["path"], cache / npgsql_library["path"])
    assets["packageFolders"] = {str(cache) + os.sep: {}}
    assets["libraries"][npgsql] = npgsql_library
    target = next(iter(assets["targets"].values()))
    target[npgsql] = {"type": "package"}
    wolverine = next(name for name in target if name.startswith("WolverineFx/"))
    target[wolverine].setdefault("dependencies", {})["Npgsql"] = npgsql.split("/")[1]
    asset_path = output / "controls/forbidden-transitive/project.assets.json"
    asset_path.write_text(json.dumps(assets))
    path = output / "controls/forbidden-transitive.json"
    path.write_text(json.dumps({**evidence, "Consumers": [{**neutral, "Assets": [str(asset_path)]}]}))
    result = run(["dotnet", ARCHITECTURE, "--validate-packages", path], log=output / "control-forbidden-transitive.log", expect_failure=True)
    if "PouryaKit.Messaging.Wolverine's closure holds Npgsql" not in result:
        raise RuntimeError(f"Transitive control failed for an unrelated reason: {result}")
    print(f"PASS negative control forbidden-transitive: {result.splitlines()[0]}", flush=True)


def prepare_history(output, *, fetch=True):
    shallow = run(["git", "-C", ROOT, "rev-parse", "--is-shallow-repository"]).strip() == "true"
    if not fetch and shallow:
        raise ValueError("--no-fetch requires full history and tags from the authenticated checkout.")
    if fetch:
        if shallow:
            run(["git", "-C", ROOT, "fetch", "--unshallow", "origin"], log=output / "history.log")
        run(["git", "-C", ROOT, "fetch", "origin", "--tags"], log=output / "tags.log")
    history = {"mode": "fetch" if fetch else "checkout", "shallow": False,
               "commit": run(["git", "-C", ROOT, "rev-parse", "HEAD"]).strip(),
               "tags": run(["git", "-C", ROOT, "tag", "--list"]).splitlines()}
    (output / "history.json").write_text(json.dumps(history, indent=2) + "\n")
    return history["commit"]


def validate(output, *, fetch=True):
    output.mkdir(parents=True, exist_ok=True)
    print(f"Evidence directory: {output}", flush=True)
    if output.is_relative_to(ROOT):
        raise ValueError("Use an evidence directory outside the checkout, so consumers cannot inherit its build settings.")
    if run(["git", "-C", ROOT, "status", "--porcelain"]).strip():
        raise ValueError("Validate a clean committed checkout so artifacts identify the tested source commit.")
    commit = prepare_history(output, fetch=fetch)
    inventory = json.loads((ROOT / "scripts/packaging/inventory.json").read_text())
    packages = inventory["packages"]
    ids = [package["id"] for package in packages]
    types = {package["id"]: package["type"] for package in packages}
    evaluated = {str(project.relative_to(ROOT)): properties(project, ["IsPackable", "PackageId", "EnablePackageValidation"])
                 for tree in ("src", "tests", "samples") for project in sorted((ROOT / tree).rglob("*.csproj"))}
    check_inventory(packages, evaluated)
    (output / "evaluated-projects.json").write_text(json.dumps(evaluated, indent=2))
    inventory_controls(packages, evaluated, output / "inventory-controls.log")
    run(["dotnet", "build", "PouryaKit.slnx", "-c", "Release", "-p:ContinuousIntegrationBuild=true"], log=output / "build.log")
    run(["dotnet", "test", "PouryaKit.slnx", "-c", "Release", "--no-build"], log=output / "runtime-tests.log")
    version = properties(ROOT / "src/PouryaKit.Domain/PouryaKit.Domain.csproj", ["MinVerVersion", "PackageVersion"], target="MinVer")
    if version["MinVerVersion"] != version["PackageVersion"]:
        raise RuntimeError(f"MinVer/build version mismatch: {version}")
    package_output = output / "packages"
    package_output.mkdir()
    run(["dotnet", "pack", "PouryaKit.slnx", "-c", "Release", "--no-build", "-p:ContinuousIntegrationBuild=true", "-o", package_output], log=output / "pack.log")
    evidence = {"Directory": str(package_output), "Version": version["PackageVersion"], "Commit": commit, "Packages": ids, "Consumers": []}
    evidence_file = output / "package-evidence.json"
    evidence_file.write_text(json.dumps(evidence, indent=2))
    run(["dotnet", ARCHITECTURE, "--validate-packages", evidence_file], log=output / "artifacts.log")
    consumers = [{"name": package["id"], "packages": [package["id"]], "excluded": []} for package in packages]
    consumers += inventory["compositions"]
    for consumer in consumers:
        name = consumer["name"]
        folder = output / "consumers" / name
        folder.mkdir(parents=True)
        consumer_config(folder, package_output)
        projects = sample_consumer(folder, consumer["source"], consumer["entries"], evidence["Version"]) if "source" in consumer else [simple_consumer(folder, consumer["packages"], types, evidence["Version"])]
        # A consumer with its own source under consumers/ exercises the packed API instead of only naming its types.
        if (source := ROOT / "scripts/packaging/consumers" / f"{name}.cs").exists():
            shutil.copy(source, folder / "Program.cs")
        # Kit references use the artifact version; third-party references retain central package management.
        central = ET.parse(folder / "Directory.Packages.props")
        for name_id in ids:
            ET.SubElement(central.getroot().find("ItemGroup"), "PackageVersion", Include=name_id, Version=evidence["Version"])
        central.write(folder / "Directory.Packages.props", encoding="unicode")
        for file in folder.rglob("*.csproj"):
            document = ET.parse(file)
            for reference in document.getroot().iter("PackageReference"):
                if reference.attrib.get("Include", "").startswith("PouryaKit."):
                    reference.attrib.pop("Version", None)
            document.write(file, encoding="unicode")
        env = isolated_env(folder)
        for project in projects:
            log = name if len(projects) == 1 else f"{name}-{project.stem}"
            run(["dotnet", "restore", project, "--configfile", folder / "NuGet.Config", "--no-http-cache", "-p:RestoreFallbackFolders="], cwd=folder, env=env, log=output / f"consumer-{log}-restore.log")
            run(["dotnet", "build", project, "-c", "Release", "--no-restore"], cwd=folder, env=env, log=output / f"consumer-{log}-build.log")
        assets = check_consumer(folder, env, ids, evidence["Version"], consumer["excluded"])
        evidence["Consumers"].append({"Name": name, "Packages": consumer["packages"], "Assets": assets})
        print(f"PASS package-only consumer {name}", flush=True)
    evidence_file.write_text(json.dumps(evidence, indent=2))
    run(["dotnet", ARCHITECTURE, "--validate-packages", evidence_file], log=output / "consumer-closures.log")
    artifact_controls(evidence, output)
    version_proof(output)
    if run(["git", "-C", ROOT, "rev-parse", "HEAD"]).strip() != commit or run(["git", "-C", ROOT, "status", "--porcelain"]).strip():
        raise RuntimeError("The checkout changed during validation.")
    artifacts = [{"id": name, "file": f"{name}.{evidence['Version']}.nupkg",
                  "sha256": hashlib.sha256((package_output / f"{name}.{evidence['Version']}.nupkg").read_bytes()).hexdigest()} for name in ids]
    (output / "manifest.json").write_text(json.dumps({"commit": commit, "version": evidence["Version"], "packages": artifacts}, indent=2) + "\n")
    print(f"PASS {len(ids)} packages, {len(consumers)} clean consumers, negative controls and MinVer proof. Manifest: {output / 'manifest.json'}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="New evidence directory outside the checkout. Default: system temp.")
    parser.add_argument("--no-fetch", action="store_true", help="Use full history and tags already fetched by CI checkout; reject shallow history.")
    arguments = parser.parse_args()
    output = arguments.output.resolve() if arguments.output else Path(tempfile.mkdtemp(prefix="pouryakit-packages-"))
    try:
        validate(output, fetch=not arguments.no_fetch)
    except (RuntimeError, ValueError, OSError, KeyError) as error:
        print(f"FAIL: {error}", flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
