# Plugin releases

The **Release plugins** workflow publishes the portable Windows x64 packages used by
the current application. It replaces the old WPF release workflow removed in #497.
It discovers IDs from committed manifests rather than maintaining a project-name map.
It never patches a version during the build: bump the manifest, implementation and
project version together, test them, and merge first.

## Run from Actions

1. Open **Actions → Release plugins → Run workflow**.
2. Select `main` and enter full plugin IDs separated by commas, for example
   `com.typewhisper.file-memory,com.typewhisper.linear`.
3. Leave **Publish** unchecked for a dry run. Download the `plugin-release` artifact
   to inspect package tests, build logs, ZIPs, source commit, hashes and catalog preview.
4. Run again with **Publish** checked to publish. A dispatch on another branch can
   build a preview, but the publication job runs only for `main`.

For a tag-driven release, push `plugin-<ID suffix>-v<version>` on an already merged
commit. For example, `plugin-file-memory-v1.4.0` selects
`com.typewhisper.file-memory` and requires manifest version `1.4.0`. Do not reuse an
existing tag. Tags outside main history or mismatched versions fail before building.

Only selected plugins with newer versions are built. Other catalog entries are
preserved. Publishing the same versions again is a no-op once the public feed agrees.
Removing or retiring a plugin is a separate catalog change, not a side effect of a
release selection. Packages without their own `Tests/*.csproj` currently fail the
release gate; add package tests before using this workflow for those packages.

## Validation and publication order

1. Test the release scripts, build selected packages and run their package tests.
2. Check the ZIP root manifest, assembly, version, minimum host version, paths,
   SHA-256 and byte size. Save the source commit and package metadata in `summary.json`.
3. Create a draft prerelease without changing the application's latest-release marker.
   Existing assets are downloaded and compared; they are never overwritten.
4. Publish the release and verify each anonymous public ZIP download.
5. Read the latest catalog, merge only the changed entries and write using the file's
   current Git blob SHA. Retry concurrent updates against a fresh snapshot; reject
   downgrades and conflicting contents for an already published version.
6. Request a GitHub Pages build and verify the public feed contains the exact entries.

The feed remains `https://typewhisper.github.io/typewhisper-win/plugins-v2.json` because
installed clients use that URL. The older `plugins.json`, other Pages files and old
release assets remain intact. Labels in Actions simply say **plugins**.

The workflow needs no personal access token: build uses read-only `GITHUB_TOKEN`;
publication gets `contents: write` and `pages: write`. GitHub documents that
[GITHUB_TOKEN commits do not trigger Pages builds](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site),
so publication explicitly uses the
[Pages build API](https://docs.github.com/en/rest/pages/pages#request-a-github-pages-build).
Releases share a concurrency group with
[`queue: max`](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)
so pending releases are queued rather than replacing one another.

## Recovery

If tests or staging fail, nothing is published. If uploading fails, rerun the failed
publication job using its original artifact: matching assets are reused, differing
assets cause an error. If public download verification fails, the catalog is not
changed. If only Pages deployment fails, rerun publication; an unchanged selected
version can still trigger and verify the missing Pages deployment. Never use asset
overwrite or delete-and-recreate as a retry strategy.

## Local entry points

Use a clean source checkout and an output directory outside it:

```powershell
python eng/Prepare-PluginRelease.py --source . --output ../plugin-release-check `
  --tag plugins-local-check --plugin-ids com.typewhisper.file-memory
python eng/Publish-PluginCatalog.py --stage ../plugin-release-check
# Explicitly publish an approved, tested artifact:
python eng/Publish-PluginCatalog.py --stage ../plugin-release-check --publish
python eng/Publish-PluginCatalog.py --stage ../plugin-release-check --verify-live
python -m unittest discover -s eng/tests -p test_plugin_release.py -v
```

The Python scripts use the authenticated `gh` CLI for GitHub access and anonymous
HTTPS for public-download checks. Preparation and default publication validation
do not mutate GitHub. Model downloads and provider credentials are not required.

## First real release: File Memory 1.4.0

- Source: `44487251c6dc8209b5aa687f0581e78d58389032` (merged #517).
- [Release](https://github.com/TypeWhisper/typewhisper-win/releases/tag/plugin-file-memory-v1.4.0).
- All 30 package tests passed in Release configuration before packaging.
- ZIP: 19,080 bytes; SHA-256
  `d4d19dfe1134ac82ab96705882b252f1fd716ae1df289f9abf92ca8aad2aaa25`.
- Public archive, GitHub Pages build and exact public catalog entry verified.
- Catalog grew from 34 to 35 entries. The legacy catalog blob remained
  `b58a54d3f32d40792be3ff7bf4b7514acf376856`.
- A second preparation/publication attempt correctly made no remote changes.
- The scripts were exercised locally with `gh`; the hosted Actions path still needs
  its first dispatch after this workflow is merged.

Release-script tests cover selection, version conflicts, concurrent catalog updates,
archive corruption, unsafe paths, failed downloads, draft recovery and Pages retry.
`actionlint` 1.7.12 accepts the workflows with only its outdated `queue` syntax warning
suppressed; the property was separately checked against the current GitHub documentation.
