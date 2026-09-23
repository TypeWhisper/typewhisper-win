"""Resolve an explicit plugin selection/tag and stage tested release artifacts."""
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


def selection(source, plugin_ids, tag, from_tag):
    if from_tag:
        match = re.fullmatch(r"plugin-([a-z0-9][a-z0-9.-]*)-v(\d+\.\d+\.\d+)", tag)
        if not match:
            raise ValueError("Expected plugin-<id suffix>-v<major.minor.patch>")
        ids = {"com.typewhisper." + match[1]}
    else:
        ids = set(filter(None, re.split(r"[,\s]+", plugin_ids)))
        if not ids:
            raise ValueError("Select at least one full plugin ID")
    projects = publisher.builder.discover_projects(source, ids)
    if from_tag and projects[0][1]["version"] != match[2]:
        raise ValueError("Tag version differs from the committed manifest; bump and merge the version first")
    return sorted(ids)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--plugin-ids", default="")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--from-tag", action="store_true")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    ids = selection(source, args.plugin_ids, args.tag, args.from_tag)
    # Avoid running package build/test code from unmerged release tags.
    if args.from_tag:
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
