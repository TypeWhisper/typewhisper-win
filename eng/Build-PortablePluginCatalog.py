"""Stage portable plugin archives and a feed from a clean source checkout."""

import argparse
import datetime
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import sys
import zipfile


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def numeric_version(value: str) -> tuple[int, int, int]:
    if not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise SystemExit(f"Unsupported catalog version: {value}")
    return tuple(map(int, value.split(".")))


def catalog_entries(document: object) -> dict[str, dict]:
    values = document.get("plugins") if isinstance(document, dict) else document
    if not isinstance(values, list):
        raise ValueError("Catalog must be an array or contain a plugins array")
    entries = {}
    for entry in values:
        plugin_id = entry["id"]
        if plugin_id in entries:
            raise ValueError(f"Duplicate catalog ID: {plugin_id}")
        numeric_version(entry["version"])
        entries[plugin_id] = entry
    return entries


def discover_projects(source: pathlib.Path, selected: set[str]) -> list[tuple[pathlib.Path, dict]]:
    projects = {}
    for portable in sorted(source.glob("plugins/*/portable.proj")) + sorted(source.glob("plugins-v2/*/portable.proj")):
        manifest = json.loads((portable.parent / "manifest.json").read_text(encoding="utf-8-sig"))
        plugin_id = manifest["id"]
        if plugin_id in projects:
            raise ValueError(f"Duplicate portable plugin ID: {plugin_id}")
        if not re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)+", plugin_id):
            raise ValueError(f"Invalid plugin ID: {plugin_id}")
        numeric_version(manifest["version"])
        projects[plugin_id] = (portable, manifest)
    if missing := selected - projects.keys():
        raise ValueError(f"Unknown portable plugin IDs: {', '.join(sorted(missing))}")
    return [value for key, value in projects.items() if not selected or key in selected]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--existing-feed", type=pathlib.Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--exclude-id", action="append", default=[], help="Plugin ID to omit from this release")
    parser.add_argument("--plugin-id", action="append", default=[], help="Build only this ID; preserve other catalog entries")
    parser.add_argument("--test", action="store_true", help="Run each changed plugin's test projects before packaging")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", args.tag):
        raise SystemExit("Release tag contains unsupported characters")
    if set(args.plugin_id) & set(args.exclude_id):
        raise SystemExit("A plugin cannot be selected and excluded")
    source = args.source.resolve(strict=True)
    output = args.output.resolve()
    status = subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=all"], cwd=source, text=True,
    )
    if status.strip():
        raise SystemExit(f"Source checkout has uncommitted or untracked files: {source}")
    source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    commit_epoch = int(subprocess.check_output(
        ["git", "show", "-s", "--format=%ct", "HEAD"], cwd=source, text=True,
    ).strip())
    zip_time = datetime.datetime.fromtimestamp(commit_epoch, datetime.timezone.utc).timetuple()[:6]
    zip_time = max(zip_time, (1980, 1, 1, 0, 0, 0))
    if output.exists():
        raise SystemExit(f"Output already exists: {output}")
    output.mkdir(parents=True)
    archives = output / "archives"
    archives.mkdir()
    logs = output / "logs"
    logs.mkdir()
    existing = json.loads(args.existing_feed.read_text(encoding="utf-8"))
    existing_entries = catalog_entries(existing)
    excluded = set(args.exclude_id)
    entries = {key: value for key, value in existing_entries.items() if key not in excluded}
    changed = []
    projects = discover_projects(source, set(args.plugin_id))
    for portable, manifest in projects:
        manifest_path = portable.parent / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        plugin_id = manifest["id"]
        version = manifest["version"]
        if plugin_id in excluded:
            print(f"SKIP  {plugin_id} (explicitly excluded)", flush=True)
            continue
        if not re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)+", plugin_id):
            raise SystemExit(f"Invalid plugin ID in {manifest_path}: {plugin_id}")
        if plugin_id in entries:
            published_version = entries[plugin_id]["version"]
            if published_version == version:
                print(f"KEEP  {plugin_id} {version}", flush=True)
                continue
            if numeric_version(version) <= numeric_version(published_version):
                raise SystemExit(
                    f"Refusing catalog downgrade for {plugin_id}: {published_version} -> {version}"
                )
        project_files = [p for p in portable.parent.glob("*.csproj") if not p.name.endswith("Tests.csproj")]
        if len(project_files) != 1:
            raise SystemExit(f"Expected one main project in {portable.parent}")
        if sys.platform != "win32":
            raise SystemExit(f"Windows x64 package staging requires Windows: {portable.parent}")
        project = project_files[0]
        project_dir = portable.parent.resolve()
        release_output = (project_dir / "bin" / "Release").resolve()
        if not project_dir.is_relative_to(source) or not release_output.is_relative_to(project_dir):
            raise SystemExit(f"Package output escapes the source checkout: {release_output}")
        if release_output.exists():
            shutil.rmtree(release_output)
        if portable.parent.name == "TypeWhisper.Plugin.SherpaOnnx":
            dependency_dir = (source / "plugins" / "TypeWhisper.Plugin.ParakeetCtc").resolve()
            dependency_output = (dependency_dir / "bin" / "Release").resolve()
            if not dependency_dir.is_relative_to(source) or not dependency_output.is_relative_to(dependency_dir):
                raise SystemExit(f"Package output escapes the source checkout: {dependency_output}")
            if dependency_output.exists():
                shutil.rmtree(dependency_output)
        print(f"BUILD {plugin_id} {version}", flush=True)
        run = subprocess.run(
            ["dotnet", "build", str(project), "-c", "Release", "-v", "quiet"],
            cwd=source, text=True, capture_output=True,
        )
        (logs / (portable.parent.name + ".log")).write_text(run.stdout + "\n" + run.stderr, encoding="utf-8")
        if run.returncode:
            raise SystemExit(f"Build failed ({run.returncode}): {project}; see {logs / (portable.parent.name + '.log')}")
        if args.test:
            tests = sorted((portable.parent / "Tests").glob("*.csproj"))
            if not tests:
                raise SystemExit(f"No package tests found for {plugin_id}; release requires test coverage")
            for test in tests:
                result = subprocess.run(["dotnet", "test", str(test), "-c", "Release", "-v", "quiet"],
                                        cwd=source, text=True, capture_output=True)
                (logs / (test.stem + ".log")).write_text(result.stdout + "\n" + result.stderr, encoding="utf-8")
                if result.returncode:
                    raise SystemExit(f"Tests failed: {test}; see {logs}")
        package = portable.parent / "bin" / "Release" / "portable-host" / "Plugins" / plugin_id
        packaged_manifest = package / "manifest.json"
        if not packaged_manifest.exists():
            raise SystemExit(f"Missing staged manifest: {package}")
        staged = json.loads(packaged_manifest.read_text(encoding="utf-8-sig"))
        if staged["id"] != plugin_id or staged["version"] != version:
            raise SystemExit(f"Staged identity/version mismatch: {package}")
        if not (package / staged["assemblyName"]).is_file():
            raise SystemExit(f"Missing plugin assembly: {package}")
        name = f"{plugin_id}-{version}-win-x64.zip"
        archive = archives / name
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as target:
            for item in sorted(package.rglob("*")):
                if item.is_file():
                    entry = zipfile.ZipInfo(item.relative_to(package).as_posix(), zip_time)
                    entry.compress_type = zipfile.ZIP_DEFLATED
                    entry.external_attr = 0o644 << 16
                    with item.open("rb") as source_file, target.open(entry, "w") as archive_file:
                        shutil.copyfileobj(source_file, archive_file)
        with zipfile.ZipFile(archive) as check:
            if check.testzip() is not None or "manifest.json" not in check.namelist():
                raise SystemExit(f"Archive integrity failed: {archive}")
        categories = manifest.get("categories") or [manifest.get("category", "integrations")]
        categories = [{"integration": "integrations", "post-processor": "post-processing"}.get(value, value)
                      for value in categories]
        entries[plugin_id] = {
            "id": plugin_id,
            "name": manifest["name"],
            "version": version,
            "minHostVersion": manifest.get("minHostVersion", "1.1.0"),
            "author": manifest.get("author", "TypeWhisper"),
            "description": manifest.get("description", ""),
            "categories": categories,
            "downloadUrl": f"https://github.com/TypeWhisper/typewhisper-win/releases/download/{args.tag}/{name}",
            "sha256": sha256(archive),
            "size": archive.stat().st_size,
            "platforms": ["windows"],
            "supportedArchitectures": ["x64"],
        }
        print(f"ZIP   {name} {archive.stat().st_size:,} bytes", flush=True)
        changed.append(entries[plugin_id])
    feed = {"plugins": sorted(entries.values(), key=lambda entry: (entry["name"].casefold(), entry["id"]))}
    (output / "plugins-v2.json").write_text(json.dumps(feed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "summary.json").write_text(json.dumps({"source": str(source), "sourceCommit": source_commit,
        "tag": args.tag, "excludedIds": sorted(excluded), "entryCount": len(entries),
        "archiveCount": len(list(archives.glob("*.zip"))), "changedPlugins": changed,
        "selectedIds": sorted(args.plugin_id), "testsRequired": args.test}, indent=2) + "\n", encoding="utf-8")
    print(f"DONE {len(entries)} entries, {len(list(archives.glob('*.zip')))} archives", flush=True)


if __name__ == "__main__":
    main()
