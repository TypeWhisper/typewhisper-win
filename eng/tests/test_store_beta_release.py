import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import store_beta_release as release


SOURCE = "a" * 40
PREVIOUS = "b" * 40
IDENTITIES = release.products()
STABLE = "applications/" + IDENTITIES["stable"]["storeProductId"]
BETA = "applications/" + IDENTITIES["beta"]["storeProductId"]
FLIGHT = STABLE + "/flights/flight-1"


class FakeGitHub:
    def __init__(self, source=SOURCE, advances=True):
        self.source = source
        self.advances = advances

    def daily_source(self, run_id):
        return self.source

    def is_ancestor(self, older, newer):
        return self.advances


class FakeStore:
    def __init__(self):
        self.calls = []
        self.resources = {}
        self.status = "PreProcessing"
        self.after_commit = None
        for product, path in (("stable", STABLE), ("beta", BETA)):
            identity = IDENTITIES[product]
            self.resources[path] = {
                "packageIdentityName": identity["packageIdentityName"],
                "publisherName": identity["packagePublisher"],
                "lastPublishedApplicationSubmission": {"id": "1"},
            }
            self.resources[path + "/submissions/1"] = self.submission("1", "applicationPackages")
        self.resources[STABLE + "/submissions/1"]["applicationPackages"][0]["version"] = "1.0.9.0"
        self.resources[FLIGHT] = {"flightId": "flight-1", "friendlyName": "Internal Beta", "groupIds": ["testers"],
                                  "lastPublishedFlightSubmission": {"id": "1"}}
        self.resources[FLIGHT + "/submissions/1"] = self.submission("1", "flightPackages")

    @staticmethod
    def submission(submission_id, package_key):
        return {"id": submission_id, "status": "Published", "notesForCertification": "Test dictation. Source revision: " + PREVIOUS,
                package_key: [{"id": "old", "fileName": "old.msix", "version": "1.1.1.0", "fileStatus": "Uploaded"}],
                "visibility": "Hidden", "pricing": {"priceId": "Free"}, "listings": {"en-us": {"description": "Beta"}},
                "ageRatings": {"USK": "12"}, "targetPublishMode": "Immediate"}

    def flights(self, app_id):
        return [{"flightId": "flight-1"}]

    def call(self, method, path, body=None):
        self.calls.append((method, path, copy.deepcopy(body)))
        if method == "GET":
            return {"status": self.status} if path.endswith("/status") else copy.deepcopy(self.resources[path])
        if method == "POST" and path.endswith("/submissions"):
            root = path.removesuffix("/submissions")
            key = "pendingFlightSubmission" if "/flights/" in path else "pendingApplicationSubmission"
            self.resources[root][key] = {"id": "2"}
            draft = copy.deepcopy(self.resources[path + "/1"])
            draft.update({"id": "2", "status": "PendingCommit", "fileUploadUrl": "https://store.blob.core.windows.net/upload?secret=sas"})
            self.resources[path + "/2"] = draft
            return copy.deepcopy(draft)
        if method == "PUT":
            self.resources[path] = copy.deepcopy(body)
            return {}
        if method == "POST" and path.endswith("/commit"):
            if self.after_commit:
                self.after_commit()
            return {}
        raise AssertionError((method, path))

    def pending(self, path, status="Certification"):
        key = "pendingFlightSubmission" if "/flights/" in path else "pendingApplicationSubmission"
        self.resources[path][key] = {"id": "pending"}
        self.resources[path + "/submissions/pending"] = {"id": "pending", "status": status}


def make_packages(root, plan):
    for destination in plan["destinations"]:
        product = release.DESTINATIONS[destination]
        identity = IDENTITIES[product]
        for rid in ("win-x64", "win-arm64"):
            directory = root / destination / rid
            directory.mkdir(parents=True)
            name = f"TypeWhisper-{product}-{rid}-{plan['version']}.msix"
            package = directory / name
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("AppxManifest.xml", '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">'
                                 f'<Identity Name="{identity["packageIdentityName"]}" Publisher="{identity["packagePublisher"]}" '
                                 f'Version="{plan["version"]}" ProcessorArchitecture="{rid[4:]}" /></Package>')
                for payload in ("TypeWhisper.exe", "TypeWhisper.dll", "TypeWhisper.pri"):
                    archive.writestr(payload, "test fixture")
            receipt = {"destination": destination, "product": product, "runtimeIdentifier": rid,
                       "version": plan["version"], "sourceRevision": plan["sourceRevision"], "identity": identity,
                       "package": name, "sha256": hashlib.sha256(package.read_bytes()).hexdigest()}
            release.write_json(directory / f"package-info-{rid}.json", receipt)


class StoreBetaReleaseTests(unittest.TestCase):
    def setUp(self):
        self.store = FakeStore()
        self.github = FakeGitHub()
        self.plan = release.make_plan(self.store, self.github, "123", IDENTITIES)
        self.store.calls.clear()
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.packages = self.root / "packages"
        self.receipt = self.root / "receipt.json"

    def test_versions_respect_all_components_and_store_reserved_revision(self):
        self.assertEqual(release.next_version(["1.0.65535.0", "1.1.7.19"]), "1.1.8.0")
        self.assertEqual(release.next_version([]), "1.1.1.0")
        for value in ("1.1.65535.0", "1.2.0.0", "2.0.0.0", "1.1.65536.0", "1.1.4-beta.0", "0.1.0.0"):
            with self.subTest(version=value), self.assertRaises(release.ReleaseError):
                release.next_version([value])

    def test_plan_uses_both_beta_targets_and_never_regular_stable(self):
        self.assertEqual(self.plan["version"], "1.1.2.0")
        self.assertEqual(self.plan["destinations"], ["internal-flight", "public-beta"])
        self.assertEqual(self.plan["targets"]["internal-flight"]["path"], FLIGHT)

    def test_busy_or_manual_draft_preserved(self):
        for path in (STABLE, BETA, FLIGHT):
            for status in ("PendingCommit", "Certification", "Publishing"):
                with self.subTest(path=path, status=status):
                    store = FakeStore()
                    store.pending(path, status)
                    result = release.make_plan(store, self.github, "123", IDENTITIES)
                    self.assertFalse(result["publish"])
                    self.assertIn(status, result["reason"])
                    self.assertTrue(all(method == "GET" for method, _, _ in store.calls))

    def test_failed_submission_requires_review(self):
        self.store.pending(FLIGHT, "CertificationFailed")
        with self.assertRaisesRegex(release.ReleaseError, "review"):
            release.make_plan(self.store, self.github, "123", IDENTITIES)

    def test_missing_first_publication_waits(self):
        del self.store.resources[BETA]["lastPublishedApplicationSubmission"]
        self.assertFalse(release.make_plan(self.store, self.github, "123", IDENTITIES)["publish"])

    def test_duplicate_source_and_partial_success_do_not_resubmit(self):
        self.store.resources[FLIGHT + "/submissions/1"]["notesForCertification"] = "Source revision: " + SOURCE
        result = release.make_plan(self.store, self.github, "123", IDENTITIES)
        self.assertEqual(result["destinations"], ["public-beta"])
        self.store.resources[BETA + "/submissions/1"]["notesForCertification"] = "Source revision: " + SOURCE
        self.assertFalse(release.make_plan(self.store, self.github, "123", IDENTITIES)["publish"])

    def test_old_daily_cannot_roll_back_source_with_a_higher_version(self):
        self.assertFalse(release.make_plan(self.store, FakeGitHub(advances=False), "123", IDENTITIES)["publish"])

    def test_validation_only_daily_does_not_read_store(self):
        self.assertFalse(release.make_plan(self.store, FakeGitHub(source=None), "123", IDENTITIES)["publish"])
        self.assertEqual(self.store.calls, [])

    def test_wrong_store_identity_fails(self):
        self.store.resources[BETA]["packageIdentityName"] = "SomeOtherApp"
        with self.assertRaisesRegex(release.ReleaseError, "identity"):
            release.make_plan(self.store, self.github, "123", IDENTITIES)

    def test_package_verification_rejects_missing_architecture_and_tampering(self):
        make_packages(self.packages, self.plan)
        self.assertEqual(len(release.validate_packages(self.packages, self.plan, IDENTITIES)), 2)
        receipt_path = next(self.packages.rglob("package-info-win-x64.json"))
        receipt = json.loads(receipt_path.read_text())
        package = receipt_path.parent / receipt["package"]
        package.write_bytes(package.read_bytes() + b"tampered")
        with self.assertRaisesRegex(release.ReleaseError, "hash"):
            release.validate_packages(self.packages, self.plan, IDENTITIES)
        receipt_path.unlink()
        with self.assertRaisesRegex(release.ReleaseError, "Both x64 and ARM64"):
            release.validate_packages(self.packages, self.plan, IDENTITIES)

    def test_provenance_rejects_wrong_source_version_product_and_identity(self):
        make_packages(self.packages, self.plan)
        path = next(self.packages.rglob("package-info-*.json"))
        original = json.loads(path.read_text())
        for field, value in (("sourceRevision", PREVIOUS), ("version", "1.1.99.0"),
                             ("product", "other"), ("identity", {}), ("package", "../other.msix")):
            receipt = {**original, field: value}
            release.write_json(path, receipt)
            with self.subTest(field=field), self.assertRaises(release.ReleaseError):
                release.validate_packages(self.packages, self.plan, IDENTITIES)

    def test_submit_preserves_metadata_and_records_actual_status_without_secrets(self):
        make_packages(self.packages, self.plan)
        uploads = []
        def upload(url, archive):
            with zipfile.ZipFile(archive) as zipped:
                uploads.append(zipped.namelist())
        result = release.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt, upload=upload)
        self.assertEqual(len(uploads), 2)
        self.assertTrue(all(len(files) == 2 for files in uploads))
        self.assertTrue(all(item["status"] == "PreProcessing" for item in result["submissions"]))
        self.assertNotIn("sas", self.receipt.read_text())
        writes = [(method, path, body) for method, path, body in self.store.calls if method != "GET"]
        self.assertFalse(any(path == STABLE + "/submissions" for _, path, _ in writes))
        self.assertFalse(any(method == "DELETE" for method, _, _ in writes))
        for method, path, body in writes:
            if method != "PUT":
                continue
            self.assertEqual(body["visibility"], "Hidden")
            self.assertEqual(body["pricing"], {"priceId": "Free"})
            self.assertEqual(body["ageRatings"], {"USK": "12"})
            self.assertEqual(body["listings"], {"en-us": {"description": "Beta"}})
            key = "flightPackages" if "/flights/" in path else "applicationPackages"
            self.assertEqual([p["fileStatus"] for p in body[key]], ["PendingDelete", "PendingUpload", "PendingUpload"])
            self.assertEqual(release.source_from_notes(body), SOURCE)

    def test_race_during_build_prevents_all_store_writes(self):
        make_packages(self.packages, self.plan)
        self.store.pending(BETA)
        with self.assertRaisesRegex(release.ReleaseError, "changed during packaging"):
            release.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt)
        self.assertTrue(all(method == "GET" for method, _, _ in self.store.calls))

    def test_race_between_destinations_preserves_the_manual_draft(self):
        make_packages(self.packages, self.plan)
        self.store.after_commit = lambda: self.store.pending(BETA)
        with self.assertRaisesRegex(release.ReleaseError, "changed before submission"):
            release.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt, upload=lambda *_: None)
        self.assertFalse(any(method == "POST" and path == BETA + "/submissions" for method, path, _ in self.store.calls))
        self.assertEqual(json.loads(self.receipt.read_text())["submissions"][0]["status"], "PreProcessing")

    def test_failed_commit_is_not_reported_as_published(self):
        make_packages(self.packages, self.plan)
        self.store.status = "CommitFailed"
        with self.assertRaisesRegex(release.ReleaseError, "CommitFailed"):
            release.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt, upload=lambda *_: None)
        self.assertEqual(json.loads(self.receipt.read_text())["submissions"][0]["status"], "CommitFailed")

    def test_upload_failure_preserves_draft_and_does_not_commit(self):
        make_packages(self.packages, self.plan)
        def fail(*_):
            raise release.ReleaseError("Upload failed")
        with self.assertRaisesRegex(release.ReleaseError, "Upload failed"):
            release.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt, upload=fail)
        self.assertFalse(any(path.endswith("/commit") for _, path, _ in self.store.calls))
        self.assertEqual(json.loads(self.receipt.read_text())["submissions"][0]["status"], "DraftCreated")

    def test_provenance_notes_replace_previous_marker(self):
        old = release.certification_notes("Test instructions.", PREVIOUS, "1.1.1.0", "122")
        new = release.certification_notes(old, SOURCE, "1.1.2.0", "123")
        self.assertEqual(new.count(release.MARKER_START), 1)
        self.assertIn("Test instructions.", new)
        self.assertNotIn(PREVIOUS, new)

    def test_sas_upload_rejects_non_azure_hosts(self):
        for url in ("https://example.com/upload?sig=secret", "http://x.blob.core.windows.net/upload", "https://blob.core.windows.net.evil.test/upload"):
            with self.subTest(url=url), self.assertRaisesRegex(release.ReleaseError, "upload host"):
                release.upload_archive(url, self.root / "does-not-exist")


class DailyTrustTests(unittest.TestCase):
    def setUp(self):
        self.github = release.GitHub()
        self.run = {"path": release.DAILY_PATH, "head_branch": "main", "head_repository": {"full_name": release.REPOSITORY},
                    "event": "schedule", "status": "completed", "conclusion": "success", "head_sha": SOURCE}
        self.jobs = {"total_count": 1, "jobs": [{"name": "release", "conclusion": "success"}]}

    def test_successful_main_daily_is_eligible(self):
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, {"status": "ahead"}]):
            self.assertEqual(self.github.daily_source("123"), SOURCE)

    def test_pr_fork_failed_run_wrong_workflow_and_non_main_are_rejected(self):
        for field, value in (("event", "pull_request"), ("head_branch", "feature"),
                             ("head_repository", {"full_name": "attacker/fork"}), ("path", ".github/workflows/other.yml"),
                             ("conclusion", "failure"), ("head_sha", "main")):
            with self.subTest(field=field), patch.object(self.github, "get", return_value={**self.run, field: value}):
                with self.assertRaises(release.ReleaseError):
                    self.github.daily_source("123")

    def test_manual_validation_only_run_is_skipped(self):
        jobs = {"total_count": 1, "jobs": [{"name": "release", "conclusion": "skipped"}]}
        with patch.object(self.github, "get", side_effect=[self.run, jobs]):
            self.assertIsNone(self.github.daily_source("123"))


class StoreProtocolTests(unittest.TestCase):
    def test_documented_listflights_endpoint_and_relative_pagination(self):
        store = release.Store.__new__(release.Store)
        store.token = "test-token"
        app_id = IDENTITIES["stable"]["storeProductId"]
        first = {"value": [{"flightId": "one"}], "@nextLink": f"applications/{app_id}/listflights/?skip=1&top=1"}
        second = {"value": [{"flightId": "two"}]}
        with patch.object(release, "request_json", side_effect=[first, second]) as request:
            self.assertEqual(store.flights(app_id), [{"flightId": "one"}, {"flightId": "two"}])
            self.assertEqual(request.call_args_list[0].args[1], release.STORE_API + f"applications/{app_id}/listflights")
            self.assertEqual(request.call_args_list[1].args[1], release.STORE_API + f"applications/{app_id}/listflights/?skip=1&top=1")

    def test_pagination_cannot_forward_access_token_to_another_host(self):
        store = release.Store.__new__(release.Store)
        store.token = "test-token"
        page = {"value": [], "@nextLink": "https://example.com/token-sink"}
        with patch.object(release, "request_json", return_value=page) as request:
            with self.assertRaisesRegex(release.ReleaseError, "pagination"):
                store.flights(IDENTITIES["stable"]["storeProductId"])
            self.assertEqual(request.call_count, 1)


if __name__ == "__main__":
    unittest.main()
