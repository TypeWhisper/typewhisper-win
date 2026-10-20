"""Submit a published Stable release to the regular Microsoft Store listing.

Builds on the Store beta automation: the same Store API client, version rules and upload
handling. A Stable submission also replaces each listing's "What's new in this version"
text and its screenshots, so the Store shows what the release brings. It never deletes a
pending submission and keeps upload URLs and credentials out of receipts and logs.
"""

import argparse
import base64
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, build_opener
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import store_beta_release as beta  # noqa: E402

ACTIVE, DAILY_PATH, FAILURES, REPOSITORY, SHA_PATTERN = beta.ACTIVE, beta.DAILY_PATH, beta.FAILURES, beta.REPOSITORY, beta.SHA_PATTERN
NoRedirect, ReleaseError = beta.NoRedirect, beta.ReleaseError
next_version, numeric_version, products = beta.next_version, beta.numeric_version, beta.products
upload_archive, write_json = beta.upload_archive, beta.write_json


LISTING_PATH = "src/TypeWhisper.Windows.StorePackage/StoreListing.json"
NOTES_ROOT = "docs/releases/store"
SCREENSHOT_REPOSITORY = "TypeWhisper/typewhisper.com"
SCREENSHOT_ROOT = "public/screenshots/"
APPROVAL_ENVIRONMENT = "store-stable"
MARKER_START = "[TypeWhisper Store stable automation]"
MARKER_END = "[/TypeWhisper Store stable automation]"
# Partner Center limits for a desktop listing.
RELEASE_NOTES_LIMIT = 1500
CAPTION_LIMIT = 200
MAX_SCREENSHOTS = 10
MAX_IMAGE_BYTES = 50 * 1024 * 1024
LONG_SIDE = (1366, 3840)
SHORT_SIDE = (768, 2160)


def request_bytes(url, limit=MAX_IMAGE_BYTES):
    request = Request(url, headers={"User-Agent": "TypeWhisper-Store-automation"})
    try:
        with build_opener(NoRedirect).open(request, timeout=120) as response:
            data = response.read(limit + 1)
    except HTTPError as error:
        raise ReleaseError(f"GET {urlsplit(url).hostname} returned HTTP {error.code}.") from None
    except (URLError, TimeoutError, OSError):
        raise ReleaseError(f"GET {urlsplit(url).hostname} failed; check service connectivity.") from None
    if len(data) > limit:
        raise ReleaseError("A screenshot is larger than the Store allows (50 MB).")
    return data


class GitHub(beta.GitHub):
    def stable_release(self, run_id):
        """The Stable release a Candidate run published, or None for a validation-only run."""
        if not re.fullmatch(r"[1-9][0-9]*", str(run_id)):
            raise ReleaseError("Candidate run ID must be a positive integer.")
        run = self.get(f"actions/runs/{run_id}")
        branch = re.fullmatch(r"release/([0-9]+)\.([0-9]+)", run.get("head_branch") or "")
        if (run.get("path") != DAILY_PATH or not branch
                or run.get("head_repository", {}).get("full_name") != REPOSITORY
                or run.get("event") != "workflow_dispatch"
                or run.get("conclusion") != "success" or run.get("status") != "completed"
                or not re.fullmatch(SHA_PATTERN, run.get("head_sha", ""))):
            raise ReleaseError("Only a successful Candidate run dispatched on a release/X.Y branch of this repository is eligible.")
        jobs = self.get(f"actions/runs/{run_id}/jobs?filter=latest&per_page=100")
        if jobs["total_count"] > 100:
            raise ReleaseError("Candidate job list is unexpectedly large.")
        if not any(job["name"] == "release" and job["conclusion"] == "success" for job in jobs["jobs"]):
            return None
        source = run["head_sha"]
        releases = [item for item in self.get("releases?per_page=50")
                    if item.get("target_commitish") == source and not item.get("draft")]
        stable = [item for item in releases if not item.get("prerelease")
                  and re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", item.get("tag_name", ""))]
        if not stable:
            return {"source": source, "branch": run["head_branch"], "version": None}
        if len(stable) != 1:
            raise ReleaseError("More than one Stable release points at this Candidate source.")
        tag = stable[0]["tag_name"]
        version = tag[1:]
        if tuple(map(int, version.split(".")[:2])) != tuple(map(int, branch.groups())):
            raise ReleaseError("The Stable version does not belong to the release branch it was built from.")
        target = self.get(f"git/ref/tags/{tag}").get("object", {})
        # An annotated tag points at a tag object that points at the commit.
        if target.get("type") == "tag" and re.fullmatch(SHA_PATTERN, target.get("sha", "")):
            target = self.get(f"git/tags/{target['sha']}").get("object", {})
        if target.get("type") != "commit" or target.get("sha") != source:
            raise ReleaseError("The Stable tag does not point at the Candidate source.")
        if not self.is_ancestor(source, run["head_branch"]):
            raise ReleaseError("The Stable source is no longer part of its release branch.")
        return {"source": source, "branch": run["head_branch"], "version": version}

    def require_reviewers(self, environment):
        """A job that names a missing environment creates it unprotected, so check before planning a submission."""
        rules = self.get(f"environments/{environment}").get("protection_rules", [])
        if not any(rule.get("type") == "required_reviewers" and rule.get("reviewers") for rule in rules):
            raise ReleaseError(f"The {environment} environment must require a reviewer before a Stable submission can be planned.")

    def text(self, path, ref):
        if ".." in path or path.startswith("/") or not re.fullmatch(SHA_PATTERN, ref):
            raise ReleaseError("Unexpected repository file request.")
        item = self.get(f"contents/{quote(path)}?ref={ref}")
        if item.get("type") != "file" or item.get("encoding") != "base64":
            raise ReleaseError(f"{path} is not a file at the release source.")
        return base64.b64decode(item["content"]).decode("utf-8-sig")


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ReleaseError("A Store screenshot is not a PNG image.")
    return struct.unpack(">II", data[16:24])


def check_screenshot(data, path):
    width, height = png_size(data)
    long_side, short_side = max(width, height), min(width, height)
    if not (LONG_SIDE[0] <= long_side <= LONG_SIDE[1] and SHORT_SIDE[0] <= short_side <= SHORT_SIDE[1]):
        raise ReleaseError(f"{path} is {width}x{height}; the Store needs 1366x768 up to 3840x2160.")


def screenshot_url(repository, revision, path):
    return f"https://raw.githubusercontent.com/{repository}/{revision}/{quote(path)}"


def load_listing(github, source, version, fetch=request_bytes):
    """Reads the listing configuration and this version's notes at the release source."""
    try:
        config = json.loads(github.text(LISTING_PATH, source))
    except json.JSONDecodeError:
        raise ReleaseError(f"{LISTING_PATH} is not valid JSON.") from None
    repository, revision = config.get("screenshotRepository"), config.get("screenshotRevision", "")
    if repository != SCREENSHOT_REPOSITORY or not re.fullmatch(SHA_PATTERN, revision):
        raise ReleaseError(f"Screenshots must come from a full commit of {SCREENSHOT_REPOSITORY}.")
    listings, downloads = {}, {}
    for language, entry in (config.get("listings") or {}).items():
        if not re.fullmatch(r"[a-z]{2,3}(-[A-Za-z0-9]{2,8})*", language):
            raise ReleaseError(f"Invalid listing language {language!r}.")
        notes = github.text(f"{NOTES_ROOT}/v{version}/{language}.txt", source).strip().replace("\r\n", "\n")
        if not notes or len(notes) > RELEASE_NOTES_LIMIT:
            raise ReleaseError(f"The {language} Store notes for {version} must have 1 to {RELEASE_NOTES_LIMIT} characters.")
        shots = entry.get("screenshots") or []
        if not 1 <= len(shots) <= MAX_SCREENSHOTS:
            raise ReleaseError(f"The {language} listing needs 1 to {MAX_SCREENSHOTS} screenshots.")
        screenshots = []
        for shot in shots:
            path, caption = shot.get("path", ""), (shot.get("caption") or "").strip()
            if not path.startswith(SCREENSHOT_ROOT) or ".." in path or not path.endswith(".png"):
                raise ReleaseError(f"Unexpected screenshot path {path!r}.")
            if not caption or len(caption) > CAPTION_LIMIT:
                raise ReleaseError(f"Screenshot captions need 1 to {CAPTION_LIMIT} characters ({path}).")
            if path not in downloads:
                data = fetch(screenshot_url(repository, revision, path))
                check_screenshot(data, path)
                downloads[path] = hashlib.sha256(data).hexdigest()
            screenshots.append({"path": path, "caption": caption, "sha256": downloads[path]})
        listings[language] = {"releaseNotes": notes, "screenshots": screenshots}
    if not listings:
        raise ReleaseError(f"{LISTING_PATH} lists no Store languages.")
    return {"screenshotRepository": repository, "screenshotRevision": revision, "listings": listings}


def match_language(store_language, configured):
    """Maps a Store listing key such as en-us or zh-hans-cn to its configured language."""
    key = store_language.lower()
    candidates = [language for language in configured
                  if key == language.lower() or key.startswith(language.lower() + "-")]
    return max(candidates, key=len) if candidates else None


def app_version_from_notes(submission):
    match = re.search(r"\bApp version: ([0-9]+\.[0-9]+\.[0-9]+)\b", submission.get("notesForCertification", ""))
    return match[1] if match else None


def certification_notes(previous, source, version, app_version, run_id):
    previous = re.sub(re.escape(MARKER_START) + r".*?" + re.escape(MARKER_END), "",
                      previous or "", flags=re.DOTALL).strip()
    marker = (f"{MARKER_START}\nApp version: {app_version}\nSource revision: {source}\n"
              f"Store package version: {version}\n"
              f"Release: https://github.com/{REPOSITORY}/releases/tag/v{app_version}\n"
              f"Candidate run: https://github.com/{REPOSITORY}/actions/runs/{run_id}\n{MARKER_END}")
    return marker + "\n\n" + previous


def inspect_stable(store, identities):
    """Version floor from every package of both products; only the regular submission can block."""
    versions, published, blocker = [], None, None
    stable_id = identities["stable"]["storeProductId"]

    def collect(resource, path, flight=False):
        refs = ("lastPublishedFlightSubmission", "pendingFlightSubmission") if flight else (
            "lastPublishedApplicationSubmission", "pendingApplicationSubmission")
        found = {}
        for name in refs:
            reference = resource.get(name)
            if reference and reference.get("id"):
                submission = store.call("GET", f"{path}/submissions/{reference['id']}")
                key = "flightPackages" if flight else "applicationPackages"
                versions.extend(p["version"] for p in submission.get(key, []) if p.get("version"))
                found[name] = submission
        return found

    for product, identity in identities.items():
        path = f"applications/{identity['storeProductId']}"
        app = store.call("GET", path)
        if app.get("packageIdentityName") != identity["packageIdentityName"] or app.get("publisherName") != identity["packagePublisher"]:
            raise ReleaseError(f"Store identity mismatch for {product}.")
        found = collect(app, path)
        if product == "stable":
            published = found.get("lastPublishedApplicationSubmission")
            pending = found.get("pendingApplicationSubmission")
            if pending:
                status = pending.get("status", "Unknown")
                if status in FAILURES:
                    raise ReleaseError(f"TypeWhisper: submission {pending.get('id')} is {status}; review it in Partner Center.")
                blocker = f"existing submission {pending.get('id')} ({status})"
    for flight in store.flights(stable_id):
        flight_id = flight["flightId"]
        if not re.fullmatch(r"[a-zA-Z0-9-]+", flight_id):
            raise ReleaseError("Invalid flight ID.")
        path = f"applications/{stable_id}/flights/{flight_id}"
        collect(store.call("GET", path), path, flight=True)
    return versions, published, blocker


def allocate(versions, regular, line):
    """Above every package of this line; betas of the next line stay higher, so testers keep their builds."""
    current = tuple(map(int, line.split(".")))
    if any(numeric_version(v)[:2] > current for v in regular):
        raise ReleaseError("The regular Store submission already carries a newer release line.")
    return next_version([v for v in versions if numeric_version(v)[:2] <= current], line)


def make_plan(store, github, run_id, identities, fetch=request_bytes):
    release = github.stable_release(run_id)
    plan = {"candidateRunId": str(run_id), "sourceRevision": None, "appVersion": None, "publish": False}
    if not release:
        return {**plan, "reason": "The Candidate run did not publish a release."}
    plan.update(sourceRevision=release["source"], appVersion=release["version"])
    if not release["version"]:
        return {**plan, "reason": "The Candidate run published a release candidate; only Stable releases go to the regular Store listing."}
    version = release["version"]
    line = ".".join(version.split(".")[:2])
    listing = load_listing(github, release["source"], version, fetch)
    versions, published, blocker = inspect_stable(store, identities)
    if blocker:
        return {**plan, "reason": f"Waiting for the existing Store submission: {blocker}."}
    if not published:
        return {**plan, "reason": "Waiting for the first published regular Store submission."}
    previous = app_version_from_notes(published)
    if previous and tuple(map(int, previous.split("."))) >= tuple(map(int, version.split("."))):
        return {**plan, "reason": f"The Store already has TypeWhisper {previous}; refusing to submit {version} again or roll back."}
    regular = [p["version"] for p in published.get("applicationPackages", []) if p.get("version")]
    store_version = allocate(versions, regular, line)
    languages, missing = {}, []
    for store_language in published.get("listings") or {}:
        configured = match_language(store_language, listing["listings"])
        if not configured:
            missing.append(store_language)
            continue
        languages[store_language] = {"language": configured, **listing["listings"][configured]}
    if missing or not languages:
        raise ReleaseError("Add Store notes and screenshots for every listing language: " + ", ".join(missing or ["(no listings found)"]))
    stable_id = identities["stable"]["storeProductId"]
    return {**plan, "publish": True, "version": store_version, "releaseLine": line,
            "target": {"path": f"applications/{stable_id}", "productId": stable_id, "lastPublishedId": published["id"]},
            "screenshotRepository": listing["screenshotRepository"], "screenshotRevision": listing["screenshotRevision"],
            "listings": languages, "reason": f"New Stable release {version}."}


def inspect(store, identities, line, config, fetch=request_bytes):
    """Read-only check before a Stable release: Store state, the next version and the listing mapping."""
    if not re.fullmatch(r"[1-9][0-9]*\.[0-9]+", line):
        raise ReleaseError("Invalid release line.")
    versions, published, blocker = inspect_stable(store, identities)
    report = {"releaseLine": line, "pending": blocker, "versions": sorted(set(versions), key=numeric_version)}
    if not published:
        return {**report, "reason": "No published regular submission yet."}
    regular = [p["version"] for p in published.get("applicationPackages", []) if p.get("version")]
    report.update(regular=regular, appVersion=app_version_from_notes(published), nextVersion=allocate(versions, regular, line))
    configured = config.get("listings") or {}
    checked = set()
    report["listings"] = {}
    for store_language, entry in (published.get("listings") or {}).items():
        base = entry.get("baseListing") or {}
        language = match_language(store_language, configured)
        shots = (configured.get(language) or {}).get("screenshots", [])
        for shot in shots:
            if shot["path"] not in checked:
                data = fetch(screenshot_url(config["screenshotRepository"], config["screenshotRevision"], shot["path"]))
                check_screenshot(data, shot["path"])
                checked.add(shot["path"])
        report["listings"][store_language] = {
            "language": language, "plannedScreenshots": len(shots),
            "storeScreenshots": sum(1 for image in base.get("images", []) if str(image.get("imageType", "")).lower() == "screenshot"),
            "releaseNotes": (base.get("releaseNotes") or "").split("\n", 1)[0]}
    missing = [key for key, value in report["listings"].items() if not value["language"]]
    report["reason"] = ("Every listing language is configured." if not missing
                        else "Configure these listing languages: " + ", ".join(missing))
    return report


def validate_packages(directory, plan, identities):
    expected = identities["stable"]
    packages = []
    for receipt_path in Path(directory).rglob("package-info-*.json"):
        receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
        if receipt.get("destination") != "stable" or receipt.get("runtimeIdentifier") != "win-x64":
            raise ReleaseError("Unexpected package destination or architecture.")
        if (receipt["sourceRevision"] != plan["sourceRevision"] or receipt["version"] != plan["version"]
                or receipt["product"] != "stable" or receipt["identity"] != expected):
            raise ReleaseError("Package provenance does not match the release plan.")
        filename = f"TypeWhisper-stable-win-x64-{plan['version']}.msix"
        if receipt["package"] != filename:
            raise ReleaseError("Unexpected package filename.")
        package = receipt_path.parent / filename
        with package.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != receipt["sha256"]:
                raise ReleaseError("MSIX hash does not match its receipt.")
        with zipfile.ZipFile(package) as archive:
            if not {"TypeWhisper.exe", "TypeWhisper.dll", "TypeWhisper.pri"}.issubset(archive.namelist()):
                raise ReleaseError("MSIX is missing required application payloads.")
            identity = ET.fromstring(archive.read("AppxManifest.xml")).find("{*}Identity")
            manifest = {"Name": expected["packageIdentityName"], "Publisher": expected["packagePublisher"],
                        "Version": plan["version"], "ProcessorArchitecture": "x64"}
            if identity is None or any(identity.get(key) != value for key, value in manifest.items()):
                raise ReleaseError("MSIX manifest does not match the Store listing.")
        packages.append(package)
    if len(packages) != 1:
        raise ReleaseError("Exactly one x64 package is required for the Stable submission.")
    return packages


def screenshot_files(plan, directory, fetch=request_bytes):
    """Downloads every planned screenshot once and checks it against the plan's hash."""
    files, names = {}, {}
    for store_language, listing in plan["listings"].items():
        for index, shot in enumerate(listing["screenshots"], start=1):
            path = shot["path"]
            if path not in files:
                data = fetch(screenshot_url(plan["screenshotRepository"], plan["screenshotRevision"], path))
                if hashlib.sha256(data).hexdigest() != shot["sha256"]:
                    raise ReleaseError(f"{path} changed after planning.")
                check_screenshot(data, path)
                target = Path(directory) / f"{len(files) + 1:02d}-{Path(path).stem}.png"
                target.write_bytes(data)
                files[path] = target
            names[(store_language, index)] = files[path].name
    return files, names


def updated_listings(listings, plan, names):
    result = copy.deepcopy(listings)
    for store_language, planned in plan["listings"].items():
        base = (result.get(store_language) or {}).get("baseListing")
        if base is None:
            raise ReleaseError(f"The {store_language} listing has no base listing to update.")
        base["releaseNotes"] = planned["releaseNotes"]
        images = [dict(image, fileStatus="PendingDelete") if str(image.get("imageType", "")).lower() == "screenshot"
                  else image for image in base.get("images", [])]
        images.extend({"fileName": names[(store_language, index)], "fileStatus": "PendingUpload",
                       "imageType": "Screenshot", "description": shot["caption"]}
                      for index, shot in enumerate(planned["screenshots"], start=1))
        base["images"] = images
    return result


def submit(store, github, plan, identities, package_dir, receipt_path,
           upload=upload_archive, fetch=request_bytes, sleep=time.sleep):
    if not plan.get("publish"):
        raise ReleaseError("Release plan has no submission to publish.")
    packages = validate_packages(package_dir, plan, identities)
    # The Store may have changed while the package was built and approved. Revalidate before the first write.
    if make_plan(store, github, plan["candidateRunId"], identities, fetch) != plan:
        raise ReleaseError("Store or release state changed during packaging. Run again; no submission was created.")
    receipt = {"appVersion": plan["appVersion"], "sourceRevision": plan["sourceRevision"], "version": plan["version"],
               "candidateRunId": plan["candidateRunId"], "submissions": []}
    write_json(receipt_path, receipt)
    path = plan["target"]["path"]
    if path != "applications/" + identities["stable"]["storeProductId"]:
        raise ReleaseError("Refusing to submit to an unexpected Store destination.")
    with tempfile.TemporaryDirectory(prefix="store-stable-") as temporary:
        shots, names = screenshot_files(plan, temporary, fetch)
        live = store.call("GET", path)
        if live.get("pendingApplicationSubmission") or live.get("lastPublishedApplicationSubmission", {}).get("id") != plan["target"]["lastPublishedId"]:
            raise ReleaseError("The regular submission changed before submission; existing drafts were preserved.")
        draft = store.call("POST", path + "/submissions")
        submission_path = path + "/submissions/" + draft["id"]
        entry = {"destination": "stable", "productId": plan["target"]["productId"], "submissionId": draft["id"],
                 "status": "DraftCreated", "packages": [p.name for p in packages],
                 "screenshots": sorted(p.name for p in shots.values()), "languages": sorted(plan["listings"])}
        receipt["submissions"].append(entry)
        write_json(receipt_path, receipt)
        payload = copy.deepcopy(draft)
        for package in payload.get("applicationPackages", []):
            package["fileStatus"] = "PendingDelete"
        payload.setdefault("applicationPackages", []).extend(
            {"fileName": package.name, "fileStatus": "PendingUpload", "minimumDirectXVersion": "None",
             "minimumSystemRam": "None"} for package in packages)
        payload["listings"] = updated_listings(payload.get("listings") or {}, plan, names)
        payload["targetPublishMode"] = "Immediate"
        payload["targetPublishDate"] = ""
        payload["notesForCertification"] = certification_notes(
            draft.get("notesForCertification", ""), plan["sourceRevision"], plan["version"], plan["appVersion"], plan["candidateRunId"])
        # Description, pricing, age ratings, visibility and other listing fields remain inherited.
        store.call("PUT", submission_path, payload)
        archive = Path(temporary) / "submission.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as output:
            for package in packages:
                output.write(package, package.name)
            for shot in shots.values():
                output.write(shot, shot.name)
        upload(draft["fileUploadUrl"], archive)
    entry["status"] = "CommitRequested"
    write_json(receipt_path, receipt)
    store.call("POST", submission_path + "/commit")
    for attempt in range(19):
        status = store.call("GET", submission_path + "/status")["status"]
        entry["status"] = status
        write_json(receipt_path, receipt)
        if status in FAILURES:
            raise ReleaseError(f"Stable submission {draft['id']} failed: {status}. Review Partner Center.")
        if status in ACTIVE - {"CommitStarted"} or status == "Published":
            break
        if status not in ("CommitStarted", "PendingCommit"):
            raise ReleaseError(f"Unexpected submission status: {status}.")
        if attempt == 18:
            raise ReleaseError(f"Stable commit is not confirmed yet; inspect submission {draft['id']} before retrying.")
        sleep(10)
    print(f"stable: submission {draft['id']} is {entry['status']}.")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    plan_args = commands.add_parser("plan")
    plan_args.add_argument("--candidate-run", required=True)
    plan_args.add_argument("--output", default="artifacts/store-stable/plan.json")
    submit_args = commands.add_parser("submit")
    submit_args.add_argument("--plan", required=True)
    submit_args.add_argument("--packages", required=True)
    submit_args.add_argument("--receipt", default="artifacts/store-stable/submission-receipt.json")
    inspect_args = commands.add_parser("inspect")
    inspect_args.add_argument("--release-line", required=True)
    args = parser.parse_args()
    github, identities = GitHub(), products()
    if args.command == "inspect":
        config = json.loads((beta.ROOT / LISTING_PATH).read_text(encoding="utf-8-sig"))
        report = inspect(beta.Store(), identities, args.release_line, config)
        print(json.dumps(report, indent=2, ensure_ascii=False))
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write("## Store Stable inspection\n\n" + report["reason"] + "\n\n```json\n"
                              + json.dumps(report, indent=2, ensure_ascii=False) + "\n```\n")
        return
    if args.command == "plan":
        # A release candidate or validation-only run never needs Store credentials.
        release = github.stable_release(args.candidate_run)
        plan = (make_plan(beta.Store(), github, args.candidate_run, identities) if release and release["version"]
                else make_plan(None, github, args.candidate_run, identities))
        if plan["publish"]:
            github.require_reviewers(APPROVAL_ENVIRONMENT)
        write_json(args.output, plan)
        print(plan["reason"])
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write(f"publish={str(plan['publish']).lower()}\n")
                output.write(f"source={plan['sourceRevision'] or ''}\nversion={plan.get('version', '')}\n")
                output.write(f"app_version={plan['appVersion'] or ''}\n")
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write("## Store Stable plan\n\n" + plan["reason"] + "\n\n")
                summary.write(f"- Candidate run: {plan['candidateRunId']}\n- Source: {plan['sourceRevision']}\n")
                if plan["publish"]:
                    summary.write(f"- TypeWhisper {plan['appVersion']} as Store version {plan['version']}\n")
                    summary.write(f"- Screenshots: {plan['screenshotRepository']}@{plan['screenshotRevision']}\n")
                    for language, listing in plan["listings"].items():
                        captions = ", ".join(shot["caption"] for shot in listing["screenshots"])
                        summary.write(f"\n### {language}\n\nScreenshots: {captions}\n\n```text\n{listing['releaseNotes']}\n```\n")
    else:
        plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        receipt = submit(beta.Store(), github, plan, identities, args.packages, args.receipt)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write("## Store Stable submission\n\n")
                for submission in receipt["submissions"]:
                    summary.write(f"- {submission['submissionId']} ({submission['status']}), languages: {', '.join(submission['languages'])}\n")
                summary.write("\nMicrosoft certification still applies; the update publishes automatically once it passes.\n")


if __name__ == "__main__":
    try:
        main()
    except ReleaseError as error:
        print(f"Store stable release: {error}", file=sys.stderr)
        sys.exit(1)
