"""Publish tested packages, then update the existing WinUI catalog using gh.

Default mode validates and previews only. --publish is the explicit write boundary.
Publication is restartable: release assets are immutable and feed writes use a CAS.
"""

import argparse
import base64
import hashlib
import importlib.util
import json
import pathlib
import re
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

SPEC = importlib.util.spec_from_file_location("catalog_builder", pathlib.Path(__file__).with_name("Build-PortablePluginCatalog.py"))
builder = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(builder)
REPO = "TypeWhisper/typewhisper-win"
FEED_PATH = "plugins-v2.json"  # Existing clients depend on this URL.
FEED_URL = f"https://typewhisper.github.io/typewhisper-win/{FEED_PATH}"


def gh(*args: str, payload: dict | None = None) -> str:
    command = ["gh", *args]
    if payload is not None:
        command += ["--input", "-"]
    result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None,
                            capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"gh {args[0]} failed: {result.stderr.strip()}")
    return result.stdout


def api(path: str, payload: dict | None = None, method: str | None = None):
    args = ["api", f"repos/{REPO}/{path}"]
    if method:
        args += ["--method", method]
    return json.loads(gh(*args, payload=payload))


def snapshot():
    data = api(f"contents/{FEED_PATH}?ref=gh-pages")
    return data["sha"], json.loads(base64.b64decode(data["content"]))


def merge_feed(document, changes):
    entries = builder.catalog_entries(document)
    for entry in changes:
        old = entries.get(entry["id"])
        if old:
            before, after = builder.numeric_version(old["version"]), builder.numeric_version(entry["version"])
            if after < before:
                raise ValueError(f"Refusing catalog downgrade: {entry['id']}")
            if after == before:
                if old != entry:
                    raise ValueError(f"Published version is immutable: {entry['id']} {entry['version']}")
                continue
        entries[entry["id"]] = entry
    values = sorted(entries.values(), key=lambda e: (e["name"].casefold(), e["id"]))
    return {**document, "plugins": values} if isinstance(document, dict) else values


def validate_stage(stage):
    summary = json.loads((stage / "summary.json").read_text(encoding="utf-8"))
    if not summary.get("testsRequired") or summary.get("excludedIds"):
        raise ValueError("Publication requires package tests and must not remove catalog entries")
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", summary["tag"]):
        raise ValueError("Invalid release tag")
    if not re.fullmatch(r"[0-9a-f]{40}", summary["sourceCommit"]):
        raise ValueError("Expected a full source commit")
    changes = summary["changedPlugins"]
    builder.catalog_entries(changes)
    if len(changes) != summary["archiveCount"]:
        raise ValueError("Archive count differs from changed plugins")
    expected = set()
    for entry in changes:
        if not re.fullmatch(r"[a-z0-9]+(?:[.-][a-z0-9]+)+", entry["id"]):
            raise ValueError("Invalid package ID")
        name = f"{entry['id']}-{entry['version']}-win-x64.zip"
        expected.add(name)
        url = f"https://github.com/{REPO}/releases/download/{summary['tag']}/{name}"
        if entry["downloadUrl"] != url:
            raise ValueError("Archive URL does not match this release")
        archive = stage / "archives" / name
        if archive.stat().st_size != entry["size"] or builder.sha256(archive) != entry["sha256"]:
            raise ValueError(f"Archive hash/size mismatch: {name}")
        with zipfile.ZipFile(archive) as package:
            if package.testzip() is not None:
                raise ValueError(f"Corrupt archive: {name}")
            for item in package.namelist():
                parts = pathlib.PurePosixPath(item.replace('\\', '/'))
                if parts.is_absolute() or '..' in parts.parts or ':' in item:
                    raise ValueError(f"Unsafe archive path: {item}")
            manifest = json.loads(package.read("manifest.json"))
            for key in ("id", "version", "minHostVersion"):
                if manifest.get(key) != entry.get(key):
                    raise ValueError(f"Package/catalog {key} mismatch: {name}")
            if manifest["assemblyName"] not in package.namelist():
                raise ValueError(f"Missing plugin assembly: {name}")
    if {p.name for p in (stage / "archives").glob("*")} != expected:
        raise ValueError("Unexpected files in archive directory")
    return summary


def public_json(url):
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def verify_download(entry):
    digest, size = hashlib.sha256(), 0
    with urllib.request.urlopen(entry["downloadUrl"], timeout=60) as response:
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
            size += len(chunk)
    if size != entry["size"] or digest.hexdigest() != entry["sha256"]:
        raise ValueError(f"Public asset differs from tested package: {entry['id']}")


def find_release(tag):
    # Query by listing first: authentication/server failures must not mean "not found".
    releases = json.loads(gh("api", f"repos/{REPO}/releases", "--paginate", "--slurp"))
    return next((r for page in releases for r in page if r["tag_name"] == tag), None)


def ensure_release(stage, summary):
    tag = summary["tag"]
    release = find_release(tag)
    marker = f"Source commit: {summary['sourceCommit']}"
    if release is None:
        notes = stage / "release-notes.md"
        notes.write_text("Portable TypeWhisper plugins for Windows x64.\n\n" + marker + "\n\n" +
                         "\n".join(f"- {e['name']} {e['version']}" for e in summary["changedPlugins"]) +
                         "\n\nPackage tests passed before staging. SHA-256 and sizes are recorded in the catalog. "
                         "Model downloads and credentials are not included.\n", encoding="utf-8")
        gh("release", "create", tag, "--repo", REPO, "--target", summary["sourceCommit"],
           "--title", f"TypeWhisper Plugins · {tag}", "--notes-file", str(notes),
           "--draft", "--prerelease", "--latest=false")
        # Draft releases do not yet have a resolvable tag endpoint.
        release = find_release(tag)
        if release is None:
            raise RuntimeError("Created draft release could not be read back")
    if marker not in (release.get("body") or "") or not release["prerelease"]:
        raise ValueError("Existing release has different provenance or is not a prerelease")
    if not release["draft"]:
        if api(f"commits/{tag}")["sha"] != summary["sourceCommit"]:
            raise ValueError("Release tag points to a different source commit")
    assets = {a["name"]: a for a in release["assets"]}
    for entry in summary["changedPlugins"]:
        name = entry["downloadUrl"].rsplit('/', 1)[1]
        if name in assets:
            with tempfile.TemporaryDirectory() as directory:
                gh("release", "download", tag, "--repo", REPO, "--pattern", name, "--dir", directory)
                downloaded = pathlib.Path(directory) / name
                if downloaded.stat().st_size != entry["size"] or builder.sha256(downloaded) != entry["sha256"]:
                    raise ValueError(f"Refusing to replace existing release asset: {name}")
        else:
            if not release["draft"]:
                raise ValueError(f"Published release is missing an expected asset: {name}")
            gh("release", "upload", tag, str(stage / "archives" / name), "--repo", REPO)
    if release["draft"]:
        gh("release", "edit", tag, "--repo", REPO, "--draft=false", "--prerelease", "--latest=false")
    if api(f"commits/{tag}")["sha"] != summary["sourceCommit"]:
        raise ValueError("Published tag does not match the tested source commit")
    for entry in summary["changedPlugins"]:
        verify_download(entry)


def update_feed(changes):
    for attempt in range(4):
        sha, current = snapshot()
        updated = merge_feed(current, changes)
        if updated == current:
            return
        payload = {"message": "Publish tested portable plugin packages", "branch": "gh-pages", "sha": sha,
                   "content": base64.b64encode((json.dumps(updated, ensure_ascii=False, indent=2) + '\n').encode()).decode()}
        try:
            api(f"contents/{FEED_PATH}", payload, "PUT")
            return
        except RuntimeError as error:
            if attempt == 3 or not any(code in str(error) for code in ("409", "422")):
                raise
            time.sleep(2)
    raise RuntimeError("Catalog changed concurrently; rerun publication")


def verify_live(changes):
    entries = builder.catalog_entries(public_json(FEED_URL))
    if any(entries.get(e["id"]) != e for e in changes):
        raise ValueError("Public catalog has not reached the expected versions")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", type=pathlib.Path, required=True)
    parser.add_argument("--publish", action="store_true")
    parser.add_argument("--verify-live", action="store_true")
    args = parser.parse_args()
    stage = args.stage.resolve(strict=True)
    summary = validate_stage(stage)
    changes = summary["changedPlugins"]
    if args.verify_live:
        verify_live(changes)
        print("Public catalog verified.")
        return
    _, current = snapshot()
    merge_feed(current, changes)  # Fail before creating a release on known conflicts.
    print(f"Validated {len(changes)} changed package(s) from {summary['sourceCommit']}", flush=True)
    if not args.publish:
        print("No remote changes made.")
        return
    if not changes:
        selected = set(summary.get("selectedIds", []))
        verification = [entry for key, entry in builder.catalog_entries(current).items() if key in selected]
        if not verification:
            print("No packages selected for publication.")
            return
        try:
            verify_live(verification)
            print("Selected versions are already public. No remote changes made.")
            return
        except (ValueError, urllib.error.URLError):
            # A previous run may have committed the feed before Pages failed.
            # Recover deployment even when no package needs rebuilding.
            changes = verification
            api("pages/builds", method="POST")
            wait_for_live(changes)
            return
    # A release may use an older reviewed main commit, but never unmerged branch code.
    comparison = api(f"compare/{summary['sourceCommit']}...main")
    if comparison["status"] not in ("ahead", "identical"):
        raise ValueError("Release source is not on main")
    ensure_release(stage, summary)
    update_feed(changes)
    # GITHUB_TOKEN commits do not start a Pages build automatically.
    api("pages/builds", method="POST")
    print("Release assets verified; catalog committed and Pages build requested.", flush=True)
    wait_for_live(changes)


def wait_for_live(changes):
    for attempt in range(30):
        try:
            verify_live(changes)
            print("Public catalog verified.", flush=True)
            return
        except (ValueError, urllib.error.URLError):
            if attempt == 29:
                raise
            time.sleep(10)


if __name__ == "__main__":
    main()
