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
                package = root / "plugins-v2" / name
                package.mkdir(parents=True)
                (package / "portable.proj").touch()
                (package / "manifest.json").write_text(json.dumps(entry("com.typewhisper." + name.lower())), encoding="utf-8")
            selected = builder.discover_projects(root, {"com.typewhisper.one"})
            self.assertEqual(len(selected), 1)
            with self.assertRaises(ValueError):
                builder.discover_projects(root, {"com.typewhisper.typo"})
            (root / "plugins-v2/Two/manifest.json").write_text(json.dumps(entry("com.typewhisper.one")), encoding="utf-8")
            with self.assertRaises(ValueError):
                builder.discover_projects(root, set())

    def test_tag_selection_and_version_match(self):
        with patch.object(prepare.publisher.builder, 'discover_projects', return_value=[(None, {"version": "1.4.0"})]):
            self.assertEqual(prepare.selection(None, '', 'plugin-file-memory-v1.4.0', True), ['com.typewhisper.file-memory'])
            with self.assertRaises(ValueError):
                prepare.selection(None, '', 'plugin-file-memory-v2.0.0', True)
            with self.assertRaises(ValueError):
                prepare.selection(None, '', 'bad-tag', True)

    def test_dispatch_requires_explicit_selection(self):
        with self.assertRaises(ValueError):
            prepare.selection(None, ' , ', 'plugins-test', False)

    def test_dispatch_deduplicates_selection(self):
        with patch.object(prepare.publisher.builder, 'discover_projects', return_value=[]):
            self.assertEqual(prepare.selection(None, 'com.test.one, com.test.two com.test.one', 'plugins-test', False),
                             ['com.test.one', 'com.test.two'])

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
        self.summary = {"tag": "plugins-test", "sourceCommit": "a" * 40, "testsRequired": True,
                        "archiveCount": 1, "excludedIds": [], "changedPlugins": []}
        self.write_archive()

    def write_archive(self, extra=None):
        with zipfile.ZipFile(self.archive, 'w') as archive:
            archive.writestr("manifest.json", json.dumps(self.manifest))
            archive.writestr("Example.dll", b"test assembly fixture")
            if extra:
                archive.writestr(extra, "bad")
        self.summary["changedPlugins"] = [self.manifest | {
            "downloadUrl": f"https://github.com/{publish.REPO}/releases/download/plugins-test/{self.name}",
            "sha256": builder.sha256(self.archive), "size": self.archive.stat().st_size}]
        self.save()

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
        existing = {"tag_name": "plugins-test", "body": "Source commit: " + "a" * 40,
                    "draft": True, "prerelease": True, "assets": [{"name": self.name}]}
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
        draft = {"tag_name": "plugins-test", "body": "Source commit: " + "a" * 40,
                 "draft": True, "prerelease": True, "assets": []}
        with patch.object(publish, "find_release", side_effect=[None, draft]), \
             patch.object(publish, "gh") as gh, \
             patch.object(publish, "api", return_value={"sha": "a" * 40}), \
             patch.object(publish, "check_existing_tag"), \
             patch.object(publish, "verify_download") as download:
            publish.ensure_release(self.stage, self.summary)
        self.assertEqual([call.args[:2] for call in gh.call_args_list],
                         [("release", "create"), ("release", "upload"), ("release", "edit")])
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
        draft = {"tag_name": "plugins-test", "body": "Source commit: " + "a" * 40,
                 "draft": True, "prerelease": True, "assets": []}
        with patch.object(publish, 'find_release', return_value=draft), \
             patch.object(publish, 'gh') as gh, \
             patch.object(publish, 'api', side_effect=[[{'ref': 'refs/tags/plugins-test'}], {'sha': 'b' * 40}]):
            with self.assertRaises(ValueError):
                publish.ensure_release(self.stage, self.summary)
        self.assertFalse(any(call.args[:2] == ('release', 'edit') for call in gh.call_args_list))

    def test_absent_tag_can_be_created_when_publishing(self):
        with patch.object(publish, 'api', return_value=[]) as api:
            publish.check_existing_tag('plugins-test', 'a' * 40)
        api.assert_called_once()


if __name__ == '__main__':
    unittest.main()
