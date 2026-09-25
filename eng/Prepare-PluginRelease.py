"""Resolve one plugin release tag and stage its tested release artifacts.

Every release, whether pushed as a tag or dispatched from Actions, is named
plugin-<ID suffix>-v<version> and publishes exactly one plugin version.
"""
import argparse
import importlib.util
import json
import os
import pathlib
import re
import subprocess
import sys

spec = importlib.util.spec_from_file_location("publisher", pathlib.Path(__file__).with_name("Publish-PluginCatalog.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


TAG_PATTERN = re.compile(r"plugin-([a-z0-9][a-z0-9.-]*)-v(\d+\.\d+\.\d+)")


def selection(source, tag):
    match = TAG_PATTERN.fullmatch(tag)
    if not match:
        raise ValueError("Expected plugin-<id suffix>-v<major.minor.patch>, for example plugin-file-memory-v1.4.0")
    plugin_id = "com.typewhisper." + match[1]
    _, manifest = publisher.builder.discover_projects(source, {plugin_id})[0]
    if manifest["version"] != match[2]:
        raise ValueError(f"Tag version {match[2]} differs from the committed manifest version "
                         f"{manifest['version']}; bump and merge the version first")
    return [plugin_id]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--tag", required=True, help="plugin-<ID suffix>-v<version>")
    parser.add_argument("--require-main", action="store_true", help="Refuse a pushed tag outside main history")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    ids = selection(source, args.tag)
    # Avoid running package build/test code from unmerged release tags.
    if args.require_main:
        sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
        if publisher.api(f"compare/{sha}...main")["status"] not in ("ahead", "identical"):
            raise ValueError("Release tags must point to a commit on main")
    _, current = publisher.snapshot()
    output = args.output.resolve()
    if output.is_relative_to(source):
        raise ValueError("Staging output must be outside the clean source checkout")
    output.parent.mkdir(parents=True, exist_ok=True)
    feed = output.with_name(output.name + "-existing.json")
    feed.write_text(json.dumps(current), encoding="utf-8")
    command = [sys.executable, str(pathlib.Path(__file__).with_name("Build-PortablePluginCatalog.py")),
               "--source", str(source), "--output", str(output), "--existing-feed", str(feed),
               "--tag", args.tag, "--test"]
    for plugin_id in ids:
        command += ["--plugin-id", plugin_id]
    subprocess.run(command, check=True)
    summary = publisher.validate_stage(output)
    publisher.validate_catalog(json.loads((output / publisher.FEED_PATH).read_text(encoding="utf-8")))
    if report := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(report, "a", encoding="utf-8") as stream:
            stream.write(f"## Plugin release\n\nSource: `{summary['sourceCommit']}`\n\n")
            stream.write("| Plugin | Version | ZIP bytes | SHA-256 |\n|---|---|---:|---|\n")
            for entry in summary["changedPlugins"]:
                stream.write(f"| {entry['id']} | {entry['version']} | {entry['size']} | `{entry['sha256']}` |\n")
            if not summary["changedPlugins"]:
                stream.write("\nAll selected versions are already published. No packages changed.\n")


if __name__ == "__main__":
    main()
