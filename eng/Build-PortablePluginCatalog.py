"""Stage portable plugin archives and a feed from a clean source checkout."""

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import zipfile


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--existing-feed", type=pathlib.Path, required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--exclude-id", action="append", default=[], help="Plugin ID to omit from this release")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        raise SystemExit(f"Output already exists: {output}")
    output.mkdir(parents=True)
    archives = output / "archives"
    archives.mkdir()
    logs = output / "logs"
    logs.mkdir()
    existing = json.loads(args.existing_feed.read_text(encoding="utf-8"))
    excluded = set(args.exclude_id)
    entries = {entry["id"]: entry for entry in existing["plugins"] if entry["id"] not in excluded}
    source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    projects = sorted(source.glob("plugins/*/portable.proj")) + sorted(source.glob("plugins-v2/*/portable.proj"))
    for portable in projects:
        manifest_path = portable.parent / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        plugin_id = manifest["id"]
        version = manifest["version"]
        if plugin_id in excluded:
            print(f"SKIP  {plugin_id} (explicitly excluded)", flush=True)
            continue
        if not re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)+", plugin_id):
            raise SystemExit(f"Invalid plugin ID in {manifest_path}: {plugin_id}")
        if plugin_id in entries and entries[plugin_id]["version"] == version:
            print(f"KEEP  {plugin_id} {version}", flush=True)
            continue
        project_files = [p for p in portable.parent.glob("*.csproj") if not p.name.endswith("Tests.csproj")]
        if len(project_files) != 1:
            raise SystemExit(f"Expected one main project in {portable.parent}")
        project = project_files[0]
        print(f"BUILD {plugin_id} {version}", flush=True)
        run = subprocess.run(
            ["dotnet", "build", str(project), "-c", "Release", "-v", "quiet"],
            cwd=source, text=True, capture_output=True,
        )
        (logs / (portable.parent.name + ".log")).write_text(run.stdout + "\n" + run.stderr, encoding="utf-8")
        if run.returncode:
            raise SystemExit(f"Build failed ({run.returncode}): {project}; see {logs / (portable.parent.name + '.log')}")
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
                    target.write(item, item.relative_to(package).as_posix())
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
    feed = {"plugins": sorted(entries.values(), key=lambda entry: (entry["name"].casefold(), entry["id"]))}
    (output / "plugins-v2.json").write_text(json.dumps(feed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "summary.json").write_text(json.dumps({"source": str(source), "sourceCommit": source_commit,
        "tag": args.tag, "excludedIds": sorted(excluded), "entryCount": len(entries),
        "archiveCount": len(list(archives.glob("*.zip")))}, indent=2) + "\n", encoding="utf-8")
    print(f"DONE {len(entries)} entries, {len(list(archives.glob('*.zip')))} archives", flush=True)


if __name__ == "__main__":
    main()
