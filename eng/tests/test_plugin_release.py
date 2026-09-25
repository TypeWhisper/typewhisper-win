"""Release safety regressions; no credentials, provider calls or model downloads."""
import copy
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("publish", pathlib.Path(__file__).parents[1] / "Publish-PluginCatalog.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)
builder = publish.builder
prepare_spec = importlib.util.spec_from_file_location("prepare", pathlib.Path(__file__).parents[1] / "Prepare-PluginRelease.py")
prepare = importlib.util.module_from_spec(prepare_spec)
prepare_spec.loader.exec_module(prepare)


def entry(plugin_id="com.typewhisper.example", version="1.0.0"):
    return {"id": plugin_id, "name": plugin_id, "version": version, "sha256": "abc"}


class CatalogTests(unittest.TestCase):
    def setUp(self):
        validation = patch.object(publish, 'validate_catalog')
        validation.start()
        self.addCleanup(validation.stop)

    def test_selected_release_preserves_unrelated_entries_and_metadata(self):
        current = {"schema": 1, "plugins": [entry(), entry("com.typewhisper.other", "9.0.0")]}
        merged = publish.merge_feed(current, [entry(version="1.1.0")])
        self.assertEqual(merged["schema"], 1)
        self.assertIn(entry("com.typewhisper.other", "9.0.0"), merged["plugins"])
        self.assertEqual(current["plugins"][0]["version"], "1.0.0")

    def test_legacy_array_shape_preserved(self):
        self.assertIsInstance(publish.merge_feed([entry()], [entry(version="1.1.0")]), list)

    def test_retry_is_noop_for_identical_entry(self):
        self.assertEqual([entry()], publish.merge_feed([entry()], [entry()]))

    def test_downgrade_rejected(self):
        with self.assertRaises(ValueError):
            publish.merge_feed([entry(version="2.0.0")], [entry()])

    def test_same_version_different_binary_rejected(self):
        replacement = entry() | {"sha256": "different"}
        with self.assertRaises(ValueError):
            publish.merge_feed([entry()], [replacement])

    def test_duplicate_ids_rejected(self):
        with self.assertRaises(ValueError):
            builder.catalog_entries([entry(), entry()])

    def test_invalid_feed_shape_rejected(self):
        with self.assertRaises(ValueError):
            builder.catalog_entries({"plugins": {}})

    def test_cas_retry_merges_concurrent_plugin_release(self):
        original = {"plugins": [entry()]}
        concurrent = {"plugins": [entry(), entry("com.typewhisper.other")]}
        with patch.object(publish, "snapshot", side_effect=[("old", original), ("new", concurrent)]), \
             patch.object(publish, "api", side_effect=[RuntimeError("HTTP 409"), {}]) as api, \
             patch.object(publish.time, "sleep"):
            publish.update_feed([entry(version="1.1.0")])
        payload = api.call_args.args[1]
        merged = json.loads(publish.base64.b64decode(payload["content"]))
        self.assertIn(entry("com.typewhisper.other"), merged["plugins"])
        self.assertEqual(payload["sha"], "new")

    def test_auth_failure_not_retried_as_conflict(self):
        with patch.object(publish, "snapshot", return_value=("old", {"plugins": []})), \
             patch.object(publish, "api", side_effect=RuntimeError("HTTP 403")) as api:
            with self.assertRaises(RuntimeError):
                publish.update_feed([entry()])
            self.assertEqual(api.call_count, 1)

    def test_unknown_or_duplicate_project_selection_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for name in ("One", "Two"):
                package = root / "plugins" / name
                package.mkdir(parents=True)
                (package / "portable.proj").touch()
                (package / "manifest.json").write_text(json.dumps(entry("com.typewhisper." + name.lower())), encoding="utf-8")
            selected = builder.discover_projects(root, {"com.typewhisper.one"})
            self.assertEqual(len(selected), 1)
            with self.assertRaises(ValueError):
                builder.discover_projects(root, {"com.typewhisper.typo"})
            (root / "plugins/Two/manifest.json").write_text(json.dumps(entry("com.typewhisper.one")), encoding="utf-8")
            with self.assertRaises(ValueError):
                builder.discover_projects(root, set())

    def test_tag_selection_and_version_match(self):
        with patch.object(prepare.publisher.builder, 'discover_projects', return_value=[(None, {"version": "1.4.0"})]) as discover:
            self.assertEqual(prepare.selection(None, 'plugin-file-memory-v1.4.0'), ['com.typewhisper.file-memory'])
            discover.assert_called_once_with(None, {'com.typewhisper.file-memory'})
            with self.assertRaisesRegex(ValueError, 'differs from the committed manifest'):
                prepare.selection(None, 'plugin-file-memory-v2.0.0')

    def test_release_names_outside_the_plugin_scheme_are_rejected(self):
        # Dispatch inputs and pushed tags share one naming scheme: plugin-<suffix>-v<version>.
        for tag in ('bad-tag', 'plugins-12345', 'plugin-file-memory-v1.4', 'plugin-File-Memory-v1.4.0',
                    'plugin-one,plugin-two-v1.0.0', 'plugin--v1.0.0'):
            with self.subTest(tag=tag), patch.object(prepare.publisher.builder, 'discover_projects') as discover:
                with self.assertRaisesRegex(ValueError, 'Expected plugin-'):
                    prepare.selection(None, tag)
                discover.assert_not_called()

    def test_package_runs_both_test_types_and_preserves_failure_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "Tests").mkdir()
            (root / "logs").mkdir()
            (root / "Tests/test_sidecar.py").touch()
            # Python-only packages have a release gate too.
            with patch.object(builder.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, "passed", "")) as run:
                builder.test_package(root, root, root / "logs")
                self.assertEqual(run.call_args.args[0][1:4], ['-m', 'unittest', 'discover'])
            (root / "Tests/Example.Tests.csproj").touch()
            with patch.object(builder.subprocess, 'run', side_effect=[subprocess.CompletedProcess([], 0, "passed", ""),
                                                                    subprocess.CompletedProcess([], 1, "", "sidecar failed")]) as run:
                with self.assertRaises(ValueError):
                    builder.test_package(root, root, root / "logs")
                self.assertEqual(run.call_count, 2)
                self.assertEqual(run.call_args_list[0].args[0][:2], ['dotnet', 'test'])
            self.assertIn('sidecar failed', (root / "logs" / (root.name + '-python-tests.log')).read_text(encoding='utf-8'))

    def test_package_without_tests_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            with self.assertRaises(ValueError):
                builder.test_package(root, root, root)

    def test_package_timeout_keeps_partial_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / 'Tests').mkdir()
            (root / 'Tests/Example.Tests.csproj').touch()
            with patch.object(builder.subprocess, 'run', side_effect=subprocess.TimeoutExpired(
                    ['dotnet'], 600, output=b'partial output', stderr=b'partial error')) as run:
                with self.assertRaisesRegex(ValueError, 'Tests timed out'):
                    builder.test_package(root, root, root)
                self.assertEqual(run.call_args.kwargs['timeout'], 600)
            self.assertEqual((root / 'Example.Tests.log').read_text(), 'partial output\npartial error')


class ValidationTimeoutTests(unittest.TestCase):
    def test_host_validation_timeout_is_contextual(self):
        with patch.object(publish.subprocess, 'run', side_effect=subprocess.TimeoutExpired(['dotnet'], 300)) as run:
            with self.assertRaisesRegex(ValueError, 'Host catalog validation timed out'):
                publish.validate_catalog([])
            self.assertEqual(run.call_args.kwargs['timeout'], 300)


class StageTests(unittest.TestCase):
    def setUp(self):
        validation = patch.object(publish, 'validate_catalog')
        validation.start()
        self.addCleanup(validation.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.stage = pathlib.Path(self.temp.name)
        (self.stage / "archives").mkdir()
        self.manifest = entry() | {"minHostVersion": "1.1.5", "assemblyName": "Example.dll"}
        self.name = "com.typewhisper.example-1.0.0-win-x64.zip"
        self.archive = self.stage / "archives" / self.name
        self.latest_tag = "v1.1.0"  # the application release stays latest
        self.summary = {"tag": "plugin-example-v1.0.0", "sourceCommit": "a" * 40, "testsRequired": True,
                        "archiveCount": 1, "excludedIds": [], "changedPlugins": []}
        self.write_archive()

    def write_archive(self, extra=None):
        with zipfile.ZipFile(self.archive, 'w') as archive:
            archive.writestr("manifest.json", json.dumps(self.manifest))
            archive.writestr("Example.dll", b"test assembly fixture")
            if extra:
                archive.writestr(extra, "bad")
        self.summary["changedPlugins"] = [self.manifest | {
            "downloadUrl": f"https://github.com/{publish.REPO}/releases/download/plugin-example-v1.0.0/{self.name}",
            "sha256": builder.sha256(self.archive), "size": self.archive.stat().st_size}]
        self.save()

    def fake_api(self, path, *args, **kwargs):
        if path.startswith("commits/"):
            return {"sha": "a" * 40}
        if path == "releases/latest":
            return {"tag_name": self.latest_tag}
        raise AssertionError(f"Unexpected API call: {path}")

    def save(self):
        (self.stage / "summary.json").write_text(json.dumps(self.summary), encoding="utf-8")

    def test_valid_package(self):
        self.assertEqual(publish.validate_stage(self.stage), self.summary)

    def test_modified_archive_rejected(self):
        with self.archive.open('ab') as output:
            output.write(b"tampered")
        with self.assertRaises(ValueError):
            publish.validate_stage(self.stage)

    def test_wrong_manifest_version_rejected(self):
        self.summary["changedPlugins"][0]["minHostVersion"] = "9.0.0"
        self.save()
        with self.assertRaises(ValueError):
            publish.validate_stage(self.stage)

    def test_traversal_rejected(self):
        for unsafe in ("../escape", "/escape", "C:/escape", "..\\escape"):
            with self.subTest(unsafe=unsafe):
                self.write_archive(unsafe)
                with self.assertRaises(ValueError):
                    publish.validate_stage(self.stage)

    def test_untested_packages_and_catalog_removals_rejected(self):
        original = copy.deepcopy(self.summary)
        for change in ({"testsRequired": False}, {"excludedIds": ["com.typewhisper.other"]}):
            self.summary = original | change
            self.save()
            with self.assertRaises(ValueError):
                publish.validate_stage(self.stage)

    def test_external_download_url_rejected(self):
        self.summary["changedPlugins"][0]["downloadUrl"] = "https://example.com/package.zip"
        self.save()
        with self.assertRaises(ValueError):
            publish.validate_stage(self.stage)

    def test_unexpected_archive_rejected(self):
        (self.stage / "archives" / "extra.zip").touch()
        with self.assertRaises(ValueError):
            publish.validate_stage(self.stage)

    def test_existing_asset_not_overwritten(self):
        existing = {"tag_name": "plugin-example-v1.0.0", "body": "Source commit: " + "a" * 40,
                    "draft": True, "prerelease": False, "assets": [{"name": self.name}]}
        calls = []
        def fake_gh(*args, **kwargs):
            calls.append(args)
            if args[0] == "api":
                return json.dumps([[existing]])
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(b"wrong bytes")
                return ""
            self.fail(f"Unexpected mutation: {args}")
        with patch.object(publish, "gh", side_effect=fake_gh):
            with self.assertRaises(ValueError):
                publish.ensure_release(self.stage, self.summary)
        self.assertFalse(any(args[:2] in (("release", "upload"), ("release", "edit")) for args in calls))

    def test_download_failure_prevents_catalog_update(self):
        with patch('sys.argv', ['publish', '--stage', str(self.stage), '--publish']), \
             patch.object(publish, "snapshot", return_value=("old", {"plugins": []})), \
             patch.object(publish, "api", return_value={"status": "ahead"}), \
             patch.object(publish, "ensure_release", side_effect=ValueError("download mismatch")), \
             patch.object(publish, "update_feed") as update:
            with self.assertRaises(ValueError):
                publish.main()
            update.assert_not_called()

    def test_default_is_read_only(self):
        with patch('sys.argv', ['publish', '--stage', str(self.stage)]), \
             patch.object(publish, "snapshot", return_value=("old", {"plugins": []})), \
             patch.object(publish, "ensure_release") as release, patch.object(publish, "update_feed") as update:
            publish.main()
            release.assert_not_called()
            update.assert_not_called()

    def test_draft_creation_uploads_before_publishing(self):
        draft = {"tag_name": self.summary["tag"], "body": "Source commit: " + "a" * 40,
                 "draft": True, "prerelease": False, "assets": []}
        with patch.object(publish, "find_release", side_effect=[None, draft]), \
             patch.object(publish, "gh") as gh, \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "check_existing_tag"), \
             patch.object(publish, "verify_download") as download:
            publish.ensure_release(self.stage, self.summary)
        self.assertEqual([call.args[:2] for call in gh.call_args_list],
                         [("release", "create"), ("release", "upload"), ("release", "edit")])
        download.assert_called_once()
        # Like the macOS plugin releases: one plugin version per release, a plain
        # release rather than a prerelease, and never the repository's latest release.
        create = gh.call_args_list[0].args
        self.assertEqual(create[create.index("--title") + 1], "com.typewhisper.example Plugin v1.0.0")
        self.assertEqual(create[2], "plugin-example-v1.0.0")
        for call in gh.call_args_list:
            self.assertNotIn("--prerelease", call.args)
            if call.args[:2] in (("release", "create"), ("release", "edit")):
                self.assertIn("--latest=false", call.args)
        notes = pathlib.Path(create[create.index("--notes-file") + 1]).read_text(encoding="utf-8")
        self.assertEqual(notes, "Source commit: " + "a" * 40 + "\n")

    def test_recovered_prerelease_draft_is_published_plain(self):
        # A draft left behind by an earlier workflow may still be flagged as a prerelease.
        draft = {"tag_name": self.summary["tag"], "body": "Source commit: " + "a" * 40,
                 "draft": True, "prerelease": True, "assets": [{"name": self.name}]}
        def fake_gh(*args, **kwargs):
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(self.archive.read_bytes())
            return ""
        with patch.object(publish, "find_release", return_value=draft), \
             patch.object(publish, "gh", side_effect=fake_gh) as gh, \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "check_existing_tag"), \
             patch.object(publish, "verify_download"):
            publish.ensure_release(self.stage, self.summary)
        edit = next(call.args for call in gh.call_args_list if call.args[:2] == ("release", "edit"))
        self.assertIn("--draft=false", edit)
        self.assertIn("--prerelease=false", edit)
        self.assertIn("--latest=false", edit)
        self.assertEqual(edit[edit.index("--title") + 1], "com.typewhisper.example Plugin v1.0.0")

    def test_published_prerelease_from_earlier_run_is_normalized(self):
        # Published before the catalog update failed: clear the prerelease flag only,
        # and leave the hand-edited title and notes alone.
        published = {"tag_name": self.summary["tag"], "name": "Renamed by hand",
                     "body": "Fixed a bug.\n\nSource commit: " + "a" * 40,
                     "draft": False, "prerelease": True, "assets": [{"name": self.name}]}
        def fake_gh(*args, **kwargs):
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(self.archive.read_bytes())
            return ""
        with patch.object(publish, "find_release", return_value=published), \
             patch.object(publish, "gh", side_effect=fake_gh) as gh, \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "verify_download"):
            publish.ensure_release(self.stage, self.summary)
        edits = [call.args for call in gh.call_args_list if call.args[:2] == ("release", "edit")]
        self.assertEqual(len(edits), 1)
        self.assertIn("--prerelease=false", edits[0])
        self.assertNotIn("--draft=false", edits[0])
        self.assertNotIn("--title", edits[0])

    def test_published_prerelease_with_generated_title_gets_canonical_title(self):
        published = {"tag_name": self.summary["tag"], "name": "TypeWhisper Plugins · " + self.summary["tag"],
                     "body": "Source commit: " + "a" * 40,
                     "draft": False, "prerelease": True, "assets": [{"name": self.name}]}
        def fake_gh(*args, **kwargs):
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(self.archive.read_bytes())
            return ""
        with patch.object(publish, "find_release", return_value=published), \
             patch.object(publish, "gh", side_effect=fake_gh) as gh, \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "verify_download"):
            publish.ensure_release(self.stage, self.summary)
        edit = next(call.args for call in gh.call_args_list if call.args[:2] == ("release", "edit"))
        self.assertIn("--prerelease=false", edit)
        self.assertEqual(edit[edit.index("--title") + 1], "com.typewhisper.example Plugin v1.0.0")

    def test_plain_release_marked_latest_is_demoted_without_other_changes(self):
        # A plugin release must never shadow the application's latest release.
        self.latest_tag = self.summary["tag"]
        published = {"tag_name": self.summary["tag"], "name": "Renamed by hand",
                     "body": "Fixed a bug.\n\nSource commit: " + "a" * 40,
                     "draft": False, "prerelease": False, "assets": [{"name": self.name}]}
        def fake_gh(*args, **kwargs):
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(self.archive.read_bytes())
            return ""
        with patch.object(publish, "find_release", return_value=published), \
             patch.object(publish, "gh", side_effect=fake_gh) as gh, \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "verify_download"):
            publish.ensure_release(self.stage, self.summary)
        edits = [call.args for call in gh.call_args_list if call.args[:2] == ("release", "edit")]
        self.assertEqual(edits, [("release", "edit", self.summary["tag"], "--repo", publish.REPO, "--latest=false")])

    def test_existing_plain_release_with_matching_provenance_is_accepted(self):
        published = {"tag_name": self.summary["tag"], "body": "Fixed a bug.\n\nSource commit: " + "a" * 40,
                     "draft": False, "prerelease": False, "assets": [{"name": self.name}]}
        def fake_gh(*args, **kwargs):
            if args[:2] == ("release", "download"):
                destination = pathlib.Path(args[args.index("--dir") + 1])
                (destination / self.name).write_bytes(self.archive.read_bytes())
                return ""
            raise AssertionError(f"Unexpected mutation: {args}")
        with patch.object(publish, "find_release", return_value=published), \
             patch.object(publish, "gh", side_effect=fake_gh), \
             patch.object(publish, "api", side_effect=self.fake_api), \
             patch.object(publish, "verify_download") as download:
            publish.ensure_release(self.stage, self.summary)
        download.assert_called_once()

    def test_noop_can_recover_pages_without_releasing_again(self):
        self.archive.unlink()
        self.summary.update(changedPlugins=[], archiveCount=0, selectedIds=[entry()["id"]])
        self.save()
        with patch('sys.argv', ['publish', '--stage', str(self.stage), '--publish']), \
             patch.object(publish, "snapshot", return_value=("old", {"plugins": [entry()]})), \
             patch.object(publish, "verify_live", side_effect=ValueError("stale")), \
             patch.object(publish, "wait_for_live") as wait, patch.object(publish, "api") as api, \
             patch.object(publish, "ensure_release") as release:
            publish.main()
        api.assert_called_once_with("pages/builds", method="POST")
        wait.assert_called_once_with([entry()])
        release.assert_not_called()

    def test_unmerged_source_cannot_publish(self):
        with patch('sys.argv', ['publish', '--stage', str(self.stage), '--publish']), \
             patch.object(publish, "snapshot", return_value=("old", {"plugins": []})), \
             patch.object(publish, "api", return_value={"status": "diverged"}), \
             patch.object(publish, "ensure_release") as release:
            with self.assertRaises(ValueError):
                publish.main()
            release.assert_not_called()

    def test_verify_live_checks_retained_selected_entries(self):
        self.archive.unlink()
        self.summary.update(changedPlugins=[], archiveCount=0, selectedIds=[entry()["id"]])
        self.save()
        (self.stage / publish.FEED_PATH).write_text(json.dumps({"plugins": [entry()]}), encoding='utf-8')
        with patch('sys.argv', ['publish', '--stage', str(self.stage), '--verify-live']), \
             patch.object(publish, 'verify_live', side_effect=ValueError('stale public catalog')) as verify:
            with self.assertRaises(ValueError):
                publish.main()
        verify.assert_called_once_with([entry()])

    def test_missing_manifest_minimum_uses_host_default(self):
        self.manifest.pop('minHostVersion')
        self.write_archive()
        self.summary['changedPlugins'][0]['minHostVersion'] = '1.1.0'
        self.save()
        publish.validate_stage(self.stage)

    def test_moved_tag_prevents_draft_publication(self):
        draft = {"tag_name": "plugin-example-v1.0.0", "body": "Source commit: " + "a" * 40,
                 "draft": True, "prerelease": False, "assets": []}
        with patch.object(publish, 'find_release', return_value=draft), \
             patch.object(publish, 'gh') as gh, \
             patch.object(publish, 'api', side_effect=[[{'ref': 'refs/tags/plugin-example-v1.0.0'}], {'sha': 'b' * 40}]):
            with self.assertRaises(ValueError):
                publish.ensure_release(self.stage, self.summary)
        self.assertFalse(any(call.args[:2] == ('release', 'edit') for call in gh.call_args_list))

    def test_absent_tag_can_be_created_when_publishing(self):
        with patch.object(publish, 'api', return_value=[]) as api:
            publish.check_existing_tag('plugin-example-v1.0.0', 'a' * 40)
        api.assert_called_once()


if __name__ == '__main__':
    unittest.main()
