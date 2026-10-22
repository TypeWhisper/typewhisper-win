import copy
import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import store_stable_release as stable


SOURCE = "c" * 40
SITE = "d" * 40
EARLIER = "b" * 40  # release branch commit before the release
LATER = "a" * 40  # release branch commit after the release
ELSEWHERE = "e" * 40  # commit on another branch
IDENTITIES = stable.products()
STABLE = "applications/" + IDENTITIES["stable"]["storeProductId"]
BETA = "applications/" + IDENTITIES["beta"]["storeProductId"]
FLIGHT = STABLE + "/flights/flight-1"


def png(width=1860, height=1200, salt=b""):
    return b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + b"IHDR" + struct.pack(">II", width, height) + b"\x08\x06\x00\x00\x00" + salt


LISTING = {
    "screenshotRepository": stable.SCREENSHOT_REPOSITORY,
    "screenshotRevision": SITE,
    "listings": {
        "en": {"screenshots": [{"path": "public/screenshots/en/windows/dashboard.png", "caption": "Dashboard"},
                               {"path": "public/screenshots/en/windows/workflows.png", "caption": "Workflows"}]},
        "de": {"screenshots": [{"path": "public/screenshots/de/windows/dashboard.png", "caption": "Dashboard"}]},
        "zh": {"screenshots": [{"path": "public/screenshots/en/windows/dashboard.png", "caption": "主页"}]},
    },
}


class FakeGitHub:
    def __init__(self, version="1.2.0", source=SOURCE):
        self.version = version
        self.source = source
        self.files = {stable.LISTING_PATH: json.dumps(LISTING, ensure_ascii=False)}
        for language in LISTING["listings"]:
            self.files[f"{stable.NOTES_ROOT}/v1.2.0/{language}.txt"] = f"TypeWhisper 1.2 ({language})\r\n\r\n- Managed settings."
        # release/1.2 in order; files of commits other than SOURCE.
        self.branch = [EARLIER, SOURCE, LATER]
        self.revisions = {}

    def stable_release(self, run_id):
        if self.source is None:
            return None
        return {"source": self.source, "branch": "release/1.2", "version": self.version}

    def is_ancestor(self, older, newer):
        head = self.branch[-1] if newer == "release/1.2" else newer
        return older in self.branch and head in self.branch and self.branch.index(older) <= self.branch.index(head)

    def text(self, path, ref):
        files = self.files if ref == SOURCE else self.revisions[ref]
        if path not in files:
            raise stable.ReleaseError(f"{path} is not a file at the release source.")
        return files[path]


class FakeSite:
    def __init__(self):
        self.images = {}
        for language in ("en", "de"):
            for name in ("dashboard", "workflows"):
                self.images[f"public/screenshots/{language}/windows/{name}.png"] = png(salt=f"{language}{name}".encode())
        self.requests = []

    def __call__(self, url, limit=stable.MAX_IMAGE_BYTES):
        prefix = f"https://raw.githubusercontent.com/{stable.SCREENSHOT_REPOSITORY}/{SITE}/"
        assert url.startswith(prefix), url
        self.requests.append(url)
        return self.images[url.removeprefix(prefix)]


def listing(language, notes):
    return {"baseListing": {"description": f"Description {language}", "releaseNotes": notes, "images": [
        {"id": "logo", "fileName": "logo.png", "fileStatus": "Uploaded", "imageType": "StoreLogoSquare"},
        {"id": f"shot-{language}", "fileName": "old.png", "fileStatus": "Uploaded", "imageType": "Screenshot", "description": "Old"}]},
        "platformOverrides": {}}


class FakeStore:
    def __init__(self):
        self.calls = []
        self.status = "PreProcessing"
        self.resources = {}
        for product, path, version in (("stable", STABLE, "1.1.8.0"), ("beta", BETA, "1.3.2.0")):
            identity = IDENTITIES[product]
            self.resources[path] = {"packageIdentityName": identity["packageIdentityName"],
                                    "publisherName": identity["packagePublisher"],
                                    "lastPublishedApplicationSubmission": {"id": "1"}}
            self.resources[path + "/submissions/1"] = {
                "id": "1", "status": "Published", "notesForCertification": "Test the dictation shortcut.",
                "applicationPackages": [{"id": "old", "fileName": "old.msix", "version": version, "fileStatus": "Uploaded"}],
                "listings": {"en-us": listing("en", "1.1 notes"), "de-de": listing("de", "1.1 Notizen"),
                             "zh-hans-cn": listing("zh", "1.1 说明")},
                "pricing": {"priceId": "Free"}, "visibility": "Public", "ageRatings": {"USK": "0"},
                "targetPublishMode": "Immediate"}
        self.resources[FLIGHT] = {"flightId": "flight-1", "friendlyName": "Internal Beta", "groupIds": ["testers"],
                                  "lastPublishedFlightSubmission": {"id": "f1"}}
        self.resources[FLIGHT + "/submissions/f1"] = {"id": "f1", "status": "Published",
                                                      "flightPackages": [{"version": "1.2.9.0"}, {"version": "1.3.4.0"}]}

    def flights(self, app_id):
        return [{"flightId": "flight-1"}]

    def call(self, method, path, body=None):
        self.calls.append((method, path, copy.deepcopy(body)))
        if method == "GET":
            return {"status": self.status} if path.endswith("/status") else copy.deepcopy(self.resources[path])
        if method == "POST" and path.endswith("/submissions"):
            root = path.removesuffix("/submissions")
            self.resources[root]["pendingApplicationSubmission"] = {"id": "2"}
            draft = copy.deepcopy(self.resources[path + "/1"])
            draft.update({"id": "2", "status": "PendingCommit", "fileUploadUrl": "https://store.blob.core.windows.net/upload?sig=secret"})
            self.resources[path + "/2"] = draft
            return copy.deepcopy(draft)
        if method == "PUT":
            self.resources[path] = copy.deepcopy(body)
            return {}
        if method == "POST" and path.endswith("/commit"):
            return {}
        raise AssertionError((method, path))

    def pending(self, status="Certification"):
        self.resources[STABLE]["pendingApplicationSubmission"] = {"id": "pending"}
        self.resources[STABLE + "/submissions/pending"] = {"id": "pending", "status": status, "applicationPackages": []}


def make_package(root, plan):
    identity = IDENTITIES["stable"]
    directory = root / "stable" / "win-x64"
    directory.mkdir(parents=True)
    name = f"TypeWhisper-stable-win-x64-{plan['version']}.msix"
    package = directory / name
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("AppxManifest.xml", '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">'
                         f'<Identity Name="{identity["packageIdentityName"]}" Publisher="{identity["packagePublisher"]}" '
                         f'Version="{plan["version"]}" ProcessorArchitecture="x64" /></Package>')
        for payload in ("TypeWhisper.exe", "TypeWhisper.dll", "TypeWhisper.pri"):
            archive.writestr(payload, "test fixture")
    stable.write_json(directory / "package-info-win-x64.json", {
        "destination": "stable", "product": "stable", "runtimeIdentifier": "win-x64", "version": plan["version"],
        "sourceRevision": plan["sourceRevision"], "identity": identity, "package": name,
        "sha256": hashlib.sha256(package.read_bytes()).hexdigest()})


class StoreStablePlanTests(unittest.TestCase):
    def setUp(self):
        self.store, self.github, self.site = FakeStore(), FakeGitHub(), FakeSite()

    def plan(self, store=None, github=None):
        return stable.make_plan(store or self.store, github or self.github, "456", IDENTITIES, self.site)

    def test_stable_release_gets_the_next_version_of_its_line_with_notes_and_screenshots(self):
        plan = self.plan()
        self.assertTrue(plan["publish"])
        # Above the 1.2 beta in the flight; the 1.3 betas of main stay higher.
        self.assertEqual(plan["version"], "1.2.10.0")
        self.assertEqual(plan["appVersion"], "1.2.0")
        self.assertEqual(sorted(plan["listings"]), ["de-de", "en-us", "zh-hans-cn"])
        self.assertEqual(plan["listings"]["zh-hans-cn"]["language"], "zh")
        self.assertEqual(plan["listings"]["en-us"]["releaseNotes"], "TypeWhisper 1.2 (en)\n\n- Managed settings.")
        self.assertEqual([s["caption"] for s in plan["listings"]["en-us"]["screenshots"]], ["Dashboard", "Workflows"])
        self.assertEqual(len(self.site.requests), 3)  # shared images are downloaded once
        self.assertTrue(all(method == "GET" for method, _, _ in self.store.calls))

    def test_release_candidates_and_validation_runs_never_touch_the_store(self):
        self.assertFalse(stable.make_plan(None, FakeGitHub(version=None), "456", IDENTITIES, self.site)["publish"])
        self.assertFalse(stable.make_plan(None, FakeGitHub(source=None), "456", IDENTITIES, self.site)["publish"])
        self.assertEqual(self.site.requests, [])

    def test_pending_regular_submission_waits_and_a_failed_one_needs_review(self):
        self.store.pending("Certification")
        result = self.plan()
        self.assertFalse(result["publish"])
        self.assertIn("Certification", result["reason"])
        store = FakeStore()
        store.pending("CertificationFailed")
        with self.assertRaisesRegex(stable.ReleaseError, "review"):
            self.plan(store)

    def test_pending_beta_submissions_do_not_block_stable(self):
        self.store.resources[BETA]["pendingApplicationSubmission"] = {"id": "b2"}
        self.store.resources[BETA + "/submissions/b2"] = {"id": "b2", "status": "CertificationFailed", "applicationPackages": []}
        self.assertTrue(self.plan()["publish"])

    def test_same_or_older_app_version_is_not_submitted_again(self):
        for previous in ("1.2.0", "1.2.1"):
            store = FakeStore()
            store.resources[STABLE + "/submissions/1"]["notesForCertification"] = stable.certification_notes("", SOURCE, "1.2.10.0", previous, "1")
            with self.subTest(previous=previous):
                self.assertFalse(self.plan(store)["publish"])

    def test_regular_submission_on_a_newer_line_is_refused(self):
        self.store.resources[STABLE + "/submissions/1"]["applicationPackages"][0]["version"] = "1.3.1.0"
        with self.assertRaisesRegex(stable.ReleaseError, "newer release line"):
            self.plan()

    def test_every_store_language_needs_notes_and_screenshots(self):
        self.store.resources[STABLE + "/submissions/1"]["listings"]["ja-jp"] = listing("ja", "")
        with self.assertRaisesRegex(stable.ReleaseError, "ja-jp"):
            self.plan()
        github = FakeGitHub()
        del github.files[f"{stable.NOTES_ROOT}/v1.2.0/de.txt"]
        with self.assertRaisesRegex(stable.ReleaseError, "de.txt"):
            self.plan(FakeStore(), github)

    def test_texts_fixed_after_the_release_come_from_a_later_commit_of_its_branch(self):
        self.store.resources[STABLE + "/submissions/1"]["listings"]["ru-ru"] = listing("ru", "1.1")
        with self.assertRaisesRegex(stable.ReleaseError, "ru-ru"):
            self.plan()
        config = copy.deepcopy(LISTING)
        config["listings"]["ru"] = {"screenshots": [{"path": "public/screenshots/en/windows/dashboard.png", "caption": "Панель"}]}
        self.github.revisions[LATER] = {**self.github.files, stable.LISTING_PATH: json.dumps(config, ensure_ascii=False),
                                        f"{stable.NOTES_ROOT}/v1.2.0/ru.txt": "TypeWhisper 1.2 (ru)"}
        plan = stable.make_plan(self.store, self.github, "456", IDENTITIES, self.site, listing_revision=LATER)
        self.assertTrue(plan["publish"])
        self.assertEqual((plan["sourceRevision"], plan["listingRevision"]), (SOURCE, LATER))
        self.assertEqual(plan["listings"]["ru-ru"]["releaseNotes"], "TypeWhisper 1.2 (ru)")
        self.assertEqual(self.plan(FakeStore())["listingRevision"], SOURCE)

    def test_listing_revision_must_follow_the_release_commit_on_its_branch(self):
        for revision in (EARLIER, ELSEWHERE, "release/1.2", "a" * 39):
            with self.subTest(revision=revision), self.assertRaisesRegex(stable.ReleaseError, "listing revision"):
                stable.make_plan(self.store, self.github, "456", IDENTITIES, self.site, listing_revision=revision)

    def test_listing_limits_are_enforced(self):
        def broken(change):
            github = FakeGitHub()
            config = json.loads(github.files[stable.LISTING_PATH])
            change(config, github)
            github.files[stable.LISTING_PATH] = json.dumps(config)
            return github
        cases = {
            "notes": lambda c, g: g.files.__setitem__(f"{stable.NOTES_ROOT}/v1.2.0/en.txt", "x" * 1501),
            "no screenshots": lambda c, g: c["listings"]["en"].__setitem__("screenshots", []),
            "too many": lambda c, g: c["listings"]["en"].__setitem__("screenshots", c["listings"]["en"]["screenshots"] * 6),
            "caption": lambda c, g: c["listings"]["en"]["screenshots"][0].__setitem__("caption", ""),
            "path": lambda c, g: c["listings"]["en"]["screenshots"][0].__setitem__("path", "public/screenshots/../secret.png"),
            "repository": lambda c, g: c.__setitem__("screenshotRepository", "someone/else"),
            "revision": lambda c, g: c.__setitem__("screenshotRevision", "main"),
        }
        for name, change in cases.items():
            with self.subTest(name=name), self.assertRaises(stable.ReleaseError):
                stable.make_plan(FakeStore(), broken(change), "456", IDENTITIES, self.site)

    def test_screenshots_must_be_store_sized_pngs(self):
        for data in (png(1240, 800), png(4000, 2500), b"GIF89a" + b"\0" * 30):
            self.site.images["public/screenshots/en/windows/dashboard.png"] = data
            with self.subTest(data=data[:24]), self.assertRaises(stable.ReleaseError):
                self.plan(FakeStore())

    def test_inspection_reports_the_next_version_and_unconfigured_languages_without_writes(self):
        self.store.resources[STABLE + "/submissions/1"]["listings"]["ja-jp"] = listing("ja", "1.1 ノート")
        report = stable.inspect(self.store, IDENTITIES, "1.2", LISTING, self.site)
        self.assertEqual(report["nextVersion"], "1.2.10.0")
        self.assertEqual(report["regular"], ["1.1.8.0"])
        self.assertEqual(report["listings"]["zh-hans-cn"]["language"], "zh")
        self.assertEqual(report["listings"]["en-us"]["storeScreenshots"], 1)
        self.assertIsNone(report["listings"]["ja-jp"]["language"])
        self.assertIn("ja-jp", report["reason"])
        self.assertTrue(all(method == "GET" for method, _, _ in self.store.calls))

    def test_store_language_keys_map_to_configured_languages(self):
        configured = {"en": {}, "de": {}, "zh": {}, "zh-hant": {}}
        self.assertEqual(stable.match_language("en-us", configured), "en")
        self.assertEqual(stable.match_language("zh-hans-cn", configured), "zh")
        self.assertEqual(stable.match_language("zh-hant-tw", configured), "zh-hant")
        self.assertIsNone(stable.match_language("ja-jp", configured))
        self.assertIsNone(stable.match_language("english", configured))


class StoreStableSubmitTests(unittest.TestCase):
    def setUp(self):
        self.store, self.github, self.site = FakeStore(), FakeGitHub(), FakeSite()
        self.plan = stable.make_plan(self.store, self.github, "456", IDENTITIES, self.site)
        self.store.calls.clear()
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.packages = self.root / "packages"
        self.receipt = self.root / "receipt.json"
        make_package(self.packages, self.plan)

    def submit(self, upload=lambda *_: None):
        return stable.submit(self.store, self.github, self.plan, IDENTITIES, self.packages, self.receipt,
                             upload=upload, fetch=self.site, sleep=lambda _: None)

    def test_submission_replaces_package_notes_and_screenshots_and_keeps_the_rest(self):
        uploads = []
        def upload(url, archive):
            with zipfile.ZipFile(archive) as zipped:
                uploads.append(sorted(zipped.namelist()))
        result = self.submit(upload)
        self.assertEqual(result["submissions"][0]["status"], "PreProcessing")
        self.assertEqual(uploads, [sorted(["TypeWhisper-stable-win-x64-1.2.10.0.msix", "01-dashboard.png",
                                           "02-workflows.png", "03-dashboard.png"])])
        body = next(body for method, path, body in self.store.calls if method == "PUT")
        self.assertEqual([p["fileStatus"] for p in body["applicationPackages"]], ["PendingDelete", "PendingUpload"])
        self.assertEqual(body["targetPublishMode"], "Immediate")
        self.assertEqual((body["pricing"], body["visibility"], body["ageRatings"]), ({"priceId": "Free"}, "Public", {"USK": "0"}))
        en = body["listings"]["en-us"]["baseListing"]
        self.assertEqual(en["description"], "Description en")
        self.assertEqual(en["releaseNotes"], "TypeWhisper 1.2 (en)\n\n- Managed settings.")
        self.assertEqual([(i["imageType"], i["fileStatus"]) for i in en["images"]],
                         [("StoreLogoSquare", "Uploaded"), ("Screenshot", "PendingDelete"),
                          ("Screenshot", "PendingUpload"), ("Screenshot", "PendingUpload")])
        self.assertEqual([i.get("description") for i in en["images"][2:]], ["Dashboard", "Workflows"])
        zh = body["listings"]["zh-hans-cn"]["baseListing"]["images"]
        self.assertEqual((zh[-1]["fileName"], zh[-1]["description"]), ("01-dashboard.png", "主页"))
        self.assertEqual(body["listings"]["de-de"]["baseListing"]["images"][-1]["fileName"], "03-dashboard.png")
        self.assertIn("App version: 1.2.0", body["notesForCertification"])
        self.assertIn("Test the dictation shortcut.", body["notesForCertification"])
        self.assertNotIn("secret", self.receipt.read_text())
        self.assertFalse(any(path.startswith(BETA) or "/flights/" in path for method, path, _ in self.store.calls if method != "GET"))

    def test_submission_rechecks_the_planned_listing_revision(self):
        self.github.revisions[LATER] = {**self.github.files, f"{stable.NOTES_ROOT}/v1.2.0/en.txt": "TypeWhisper 1.2 (fixed)"}
        self.plan = stable.make_plan(self.store, self.github, "456", IDENTITIES, self.site, listing_revision=LATER)
        self.store.calls.clear()
        self.submit()
        body = next(body for method, path, body in self.store.calls if method == "PUT")
        self.assertEqual(body["listings"]["en-us"]["baseListing"]["releaseNotes"], "TypeWhisper 1.2 (fixed)")
        self.assertEqual(body["listings"]["de-de"]["baseListing"]["releaseNotes"], "TypeWhisper 1.2 (de)\n\n- Managed settings.")

    def test_screenshot_changed_after_planning_prevents_all_writes(self):
        self.site.images["public/screenshots/en/windows/workflows.png"] = png(salt=b"changed")
        with self.assertRaises(stable.ReleaseError):
            self.submit()
        self.assertTrue(all(method == "GET" for method, _, _ in self.store.calls))

    def test_store_change_during_packaging_prevents_all_writes(self):
        self.store.pending("PendingCommit")
        with self.assertRaisesRegex(stable.ReleaseError, "changed during packaging"):
            self.submit()
        self.assertTrue(all(method == "GET" for method, _, _ in self.store.calls))

    def test_package_provenance_and_failed_commit_are_reported(self):
        receipt_path = next(self.packages.rglob("package-info-win-x64.json"))
        original = json.loads(receipt_path.read_text())
        for field, value in (("destination", "internal-flight"), ("version", "1.2.11.0"), ("product", "beta"), ("sourceRevision", "e" * 40)):
            stable.write_json(receipt_path, {**original, field: value})
            with self.subTest(field=field), self.assertRaises(stable.ReleaseError):
                stable.validate_packages(self.packages, self.plan, IDENTITIES)
        stable.write_json(receipt_path, original)
        self.store.status = "CommitFailed"
        with self.assertRaisesRegex(stable.ReleaseError, "CommitFailed"):
            self.submit()
        self.assertEqual(json.loads(self.receipt.read_text())["submissions"][0]["status"], "CommitFailed")


class StableReleaseTrustTests(unittest.TestCase):
    def setUp(self):
        self.github = stable.GitHub()
        self.run = {"path": stable.DAILY_PATH, "head_branch": "release/1.2", "head_repository": {"full_name": stable.REPOSITORY},
                    "event": "workflow_dispatch", "status": "completed", "conclusion": "success", "head_sha": SOURCE}
        self.jobs = {"total_count": 1, "jobs": [{"name": "release", "conclusion": "success"}]}
        self.releases = [{"tag_name": "v1.2.0", "target_commitish": SOURCE, "prerelease": False, "draft": False},
                         {"tag_name": "v1.2.0-rc.2", "target_commitish": "e" * 40, "prerelease": True, "draft": False}]
        self.tag = {"object": {"type": "commit", "sha": SOURCE}}

    def test_published_stable_release_is_eligible(self):
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, self.releases, self.tag, {"status": "identical"}]):
            self.assertEqual(self.github.stable_release("456"), {"source": SOURCE, "branch": "release/1.2", "version": "1.2.0"})

    def test_annotated_tag_is_dereferenced_to_its_commit(self):
        annotated = {"object": {"type": "tag", "sha": "f" * 40}}
        tag_object = {"object": {"type": "commit", "sha": SOURCE}}
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, self.releases, annotated, tag_object, {"status": "identical"}]) as get:
            self.assertEqual(self.github.stable_release("456")["version"], "1.2.0")
            self.assertEqual(get.call_args_list[4].args[0], "git/tags/" + "f" * 40)
        elsewhere = {"object": {"type": "commit", "sha": "e" * 40}}
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, self.releases, annotated, elsewhere]):
            with self.assertRaisesRegex(stable.ReleaseError, "tag"):
                self.github.stable_release("456")

    def test_release_candidate_is_recognized_without_a_version(self):
        releases = [{"tag_name": "v1.2.0-rc.3", "target_commitish": SOURCE, "prerelease": True, "draft": False}]
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, releases]):
            self.assertIsNone(self.github.stable_release("456")["version"])

    def test_untrusted_runs_are_rejected(self):
        for field, value in (("event", "schedule"), ("head_branch", "main"), ("head_branch", "release/1.2-hotfix"),
                             ("head_repository", {"full_name": "attacker/fork"}), ("path", ".github/workflows/other.yml"),
                             ("conclusion", "failure"), ("head_sha", "release/1.2")):
            with self.subTest(field=field), patch.object(self.github, "get", return_value={**self.run, field: value}):
                with self.assertRaises(stable.ReleaseError):
                    self.github.stable_release("456")

    def test_tag_and_branch_must_match_the_source(self):
        moved = {"object": {"type": "commit", "sha": "e" * 40}}
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, self.releases, moved]):
            with self.assertRaisesRegex(stable.ReleaseError, "tag"):
                self.github.stable_release("456")
        other_line = [{"tag_name": "v1.3.0", "target_commitish": SOURCE, "prerelease": False, "draft": False}]
        with patch.object(self.github, "get", side_effect=[self.run, self.jobs, other_line]):
            with self.assertRaisesRegex(stable.ReleaseError, "release branch"):
                self.github.stable_release("456")

    def test_approval_environment_must_require_a_reviewer(self):
        protected = {"protection_rules": [{"type": "branch_policy"},
                                          {"type": "required_reviewers", "reviewers": [{"type": "User"}]}]}
        with patch.object(self.github, "get", return_value=protected):
            self.github.require_reviewers("store-stable")
        for environment in ({"protection_rules": [{"type": "branch_policy"}]}, {"protection_rules": []},
                            {"protection_rules": [{"type": "required_reviewers", "reviewers": []}]}):
            with self.subTest(environment=environment), patch.object(self.github, "get", return_value=environment):
                with self.assertRaisesRegex(stable.ReleaseError, "reviewer"):
                    self.github.require_reviewers("store-stable")

    def test_validation_only_run_is_skipped(self):
        jobs = {"total_count": 1, "jobs": [{"name": "release", "conclusion": "skipped"}]}
        with patch.object(self.github, "get", side_effect=[self.run, jobs]):
            self.assertIsNone(self.github.stable_release("456"))


if __name__ == "__main__":
    unittest.main()
