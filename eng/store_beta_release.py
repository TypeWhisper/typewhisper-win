"""Submit verified Daily sources to the two Microsoft Store beta destinations.

Uses the documented Store submission API, never deletes a pending submission, and
keeps upload URLs and credentials out of receipts and logs. No app build is run here.
"""

import argparse
import copy
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urljoin, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = "TypeWhisper/typewhisper-win"
STORE_API = "https://manage.devcenter.microsoft.com/v1.0/my/"
DAILY_PATH = ".github/workflows/winui-daily-candidate.yml"
FLIGHT_NAME = "Internal Beta"
DESTINATIONS = {"internal-flight": "stable", "public-beta": "beta"}
SHA_PATTERN = r"[0-9a-f]{40}"
MARKER_START = "[TypeWhisper Store automation]"
MARKER_END = "[/TypeWhisper Store automation]"
FAILURES = {"Canceled", "CommitFailed", "PreProcessingFailed", "CertificationFailed",
            "ReleaseFailed", "PublishFailed"}
ACTIVE = {"CommitStarted", "PreProcessing", "Certification", "Release",
          "PendingPublication", "Publishing"}


class ReleaseError(RuntimeError):
    pass


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request_json(method, url, headers=None, data=None):
    """Never include response bodies, credentials or query strings in errors."""
    request = Request(url, data=data, headers=headers or {}, method=method)
    try:
        with build_opener(NoRedirect).open(request, timeout=120) as response:
            body = response.read()
            return json.loads(body) if body else {}
    except HTTPError as error:
        raise ReleaseError(f"{method} {urlsplit(url).hostname} returned HTTP {error.code}.") from None
    except (URLError, TimeoutError, OSError):
        raise ReleaseError(f"{method} {urlsplit(url).hostname} failed; check service connectivity.") from None


class Store:
    def __init__(self):
        values = {key: os.environ.get(f"PARTNER_CENTER_{key}", "")
                  for key in ("TENANT_ID", "CLIENT_ID", "CLIENT_SECRET")}
        if not all(values.values()):
            raise ReleaseError("Configure PARTNER_CENTER_TENANT_ID, CLIENT_ID and CLIENT_SECRET in the store-beta environment.")
        for key in ("TENANT_ID", "CLIENT_ID"):
            if not re.fullmatch(r"[0-9a-fA-F-]{36}", values[key]):
                raise ReleaseError(f"PARTNER_CENTER_{key} must be a GUID.")
        token = request_json(
            "POST", f"https://login.microsoftonline.com/{values['TENANT_ID']}/oauth2/token",
            {"Content-Type": "application/x-www-form-urlencoded"},
            urlencode({"grant_type": "client_credentials", "client_id": values["CLIENT_ID"],
                       "client_secret": values["CLIENT_SECRET"],
                       "resource": "https://manage.devcenter.microsoft.com"}).encode(),
        )
        self.token = token["access_token"]

    def call(self, method, path, body=None):
        if not path.startswith("applications/") or ".." in path or "?" in path:
            raise ReleaseError("Unexpected Store resource path.")
        return request_json(method, STORE_API + path,
                            {"Authorization": "Bearer " + self.token, "Content-Type": "application/json"},
                            json.dumps(body).encode() if body is not None else None)

    def flights(self, app_id):
        # The API defaults to only ten entries. Follow its documented pagination.
        base = STORE_API + f"applications/{app_id}/listflights"
        url = base
        results = []
        seen = set()
        while url:
            parsed, expected = urlsplit(url), urlsplit(base)
            if (parsed.scheme != expected.scheme or parsed.netloc != expected.netloc
                    or parsed.path.rstrip('/') != expected.path or url in seen):
                raise ReleaseError("Unexpected Store flight pagination.")
            seen.add(url)
            page = request_json("GET", url, {"Authorization": "Bearer " + self.token})
            results.extend(page["value"])
            next_link = page.get("@nextLink") or page.get("@odata.nextLink")
            url = urljoin(STORE_API, next_link) if next_link else None
        return results


class GitHub:
    def get(self, path):
        token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
        if not token:
            raise ReleaseError("A GitHub token with contents:read and actions:read is required.")
        return request_json("GET", f"https://api.github.com/repos/{REPOSITORY}/{path}",
                            {"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                             "X-GitHub-Api-Version": "2022-11-28"})

    def is_ancestor(self, older, newer):
        if older == newer:
            return True
        comparison = self.get(f"compare/{older}...{newer}")
        return comparison["status"] in ("ahead", "identical")

    def daily_source(self, run_id):
        if not re.fullmatch(r"[1-9][0-9]*", str(run_id)):
            raise ReleaseError("Daily run ID must be a positive integer.")
        run = self.get(f"actions/runs/{run_id}")
        if (run.get("path") != DAILY_PATH or run.get("head_branch") != "main"
                or run.get("head_repository", {}).get("full_name") != REPOSITORY
                or run.get("event") not in ("schedule", "workflow_dispatch")
                or run.get("conclusion") != "success" or run.get("status") != "completed"
                or not re.fullmatch(SHA_PATTERN, run.get("head_sha", ""))):
            raise ReleaseError("Only a successful scheduled or manually published Daily from this repository's main is eligible.")
        jobs = self.get(f"actions/runs/{run_id}/jobs?filter=latest&per_page=100")
        if jobs["total_count"] > 100:
            raise ReleaseError("Daily job list is unexpectedly large.")
        if not any(job["name"] == "release" and job["conclusion"] == "success" for job in jobs["jobs"]):
            return None  # A manual validation-only run is intentionally not published.
        source = run["head_sha"]
        if not self.is_ancestor(source, "main"):
            raise ReleaseError("Daily source is no longer part of main.")
        return source


def products():
    return json.loads((ROOT / "src/TypeWhisper.Windows.StorePackage/StoreProducts.json").read_text(encoding="utf-8-sig"))


def numeric_version(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", value):
        raise ReleaseError("Invalid numeric Store package version.")
    parts = tuple(map(int, value.split(".")))
    if not 0 < parts[0] <= 65535 or any(part > 65535 for part in parts):
        raise ReleaseError("Store version component is out of range.")
    return parts


def next_version(versions, release_line="1.1"):
    if not re.fullmatch(r"[1-9][0-9]*\.[0-9]+", release_line):
        raise ReleaseError("Invalid release line.")
    major, minor = map(int, release_line.split("."))
    floor = max([numeric_version(v) for v in versions] + [(major, minor, 0, 0)])
    if floor[:2] != (major, minor) or floor[2] >= 65535 or major > 65535 or minor > 65535:
        raise ReleaseError("Store version allocation needs a newer release line; refusing to lower a version or wrap a component.")
    return f"{major}.{minor}.{floor[2] + 1}.0"


def source_from_notes(submission):
    # Also recognizes the provenance recorded in the manually submitted first beta.
    match = re.search(r"\bsource revision[: ]+(" + SHA_PATTERN + r")\b",
                      submission.get("notesForCertification", ""), flags=re.IGNORECASE)
    return match[1].lower() if match else None


def certification_notes(previous, source, version, daily_run):
    previous = re.sub(re.escape(MARKER_START) + r".*?" + re.escape(MARKER_END), "",
                      previous or "", flags=re.DOTALL).strip()
    # Remove the obsolete one-line provenance from the first manual submission.
    previous = re.sub(r"^TypeWhisper 1\.1 beta \(Store package version [0-9.]+\), built from source revision "
                      + SHA_PATTERN + r"\.\s*", "", previous, flags=re.IGNORECASE)
    marker = (f"{MARKER_START}\nSource revision: {source}\nStore package version: {version}\n"
              f"Validated Daily: https://github.com/{REPOSITORY}/actions/runs/{daily_run}\n"
              f"{MARKER_END}")
    return marker + "\n\n" + previous


def inspect_store(store, identities):
    """Read all version floors, while limiting write destinations to our two betas."""
    versions, blockers, targets = [], [], {}

    def submissions(resource, path, flight=False, label=""):
        refs = ("lastPublishedFlightSubmission", "pendingFlightSubmission") if flight else (
            "lastPublishedApplicationSubmission", "pendingApplicationSubmission")
        published = None
        for index, name in enumerate(refs):
            reference = resource.get(name)
            if not reference or not reference.get("id"):
                continue
            submission = store.call("GET", f"{path}/submissions/{reference['id']}")
            key = "flightPackages" if flight else "applicationPackages"
            versions.extend(package["version"] for package in submission.get(key, []) if package.get("version"))
            if index == 0:
                published = submission
            else:
                status = submission.get("status", "Unknown")
                if status in FAILURES:
                    raise ReleaseError(f"{label}: submission {reference['id']} is {status}; review it in Partner Center.")
                blockers.append(f"{label}: existing submission {reference['id']} ({status})")
        return published

    stable_id = identities["stable"]["storeProductId"]
    beta_id = identities["beta"]["storeProductId"]
    for product, identity in identities.items():
        path = f"applications/{identity['storeProductId']}"
        app = store.call("GET", path)
        if app.get("packageIdentityName") != identity["packageIdentityName"] or app.get("publisherName") != identity["packagePublisher"]:
            raise ReleaseError(f"Store identity mismatch for {product}.")
        published = submissions(app, path, label=identity["displayName"])
        if product == "beta":
            targets["public-beta"] = {"path": path, "published": published, "productId": beta_id}
    matches = []
    for flight in store.flights(stable_id):
        flight_id = flight["flightId"]
        if not re.fullmatch(r"[a-zA-Z0-9-]+", flight_id):
            raise ReleaseError("Invalid flight ID.")
        path = f"applications/{stable_id}/flights/{flight_id}"
        current = store.call("GET", path)
        published = submissions(current, path, flight=True, label=current["friendlyName"])
        if current["friendlyName"] == FLIGHT_NAME:
            if not current.get("groupIds"):
                raise ReleaseError("Internal Beta has no tester group.")
            matches.append({"path": path, "published": published, "productId": stable_id, "flightId": flight_id})
    if len(matches) != 1:
        raise ReleaseError("Expected exactly one existing Internal Beta flight.")
    targets["internal-flight"] = matches[0]
    return versions, blockers, targets


def make_plan(store, github, run_id, identities, release_line="1.1"):
    source = github.daily_source(run_id)
    plan = {"dailyRunId": str(run_id), "sourceRevision": source, "releaseLine": release_line,
            "publish": False, "destinations": []}
    if not source:
        return {**plan, "reason": "Daily did not publish a release (validation-only run)."}
    versions, blockers, targets = inspect_store(store, identities)
    if blockers:
        return {**plan, "reason": "Waiting for existing Store submissions. " + "; ".join(blockers)}
    selected = {}
    for destination in DESTINATIONS:
        target = targets[destination]
        published = target["published"]
        if not published:
            return {**plan, "reason": f"Waiting for the first published {destination} submission."}
        previous_source = source_from_notes(published)
        if previous_source == source:
            continue
        if previous_source and not github.is_ancestor(previous_source, source):
            return {**plan, "reason": f"Daily source does not advance {destination}; refusing a source rollback."}
        selected[destination] = {key: value for key, value in target.items() if key != "published"}
        selected[destination]["lastPublishedId"] = published["id"]
    if not selected:
        return {**plan, "reason": "Both beta destinations already contain this Daily source."}
    return {**plan, "publish": True, "destinations": list(selected), "targets": selected,
            "version": next_version(versions, release_line), "reason": "New validated Daily source."}


def validate_packages(directory, plan, identities):
    receipts, result = {}, {}
    for receipt_path in Path(directory).rglob("package-info-*.json"):
        receipt = json.loads(receipt_path.read_text(encoding="utf-8-sig"))
        destination, rid = receipt["destination"], receipt["runtimeIdentifier"]
        if destination not in plan["destinations"] or rid not in ("win-x64", "win-arm64"):
            raise ReleaseError("Unexpected package destination or architecture.")
        key = (destination, rid)
        if key in receipts:
            raise ReleaseError("Duplicate package receipt.")
        receipts[key] = receipt
        product = DESTINATIONS[destination]
        expected = identities[product]
        if (receipt["sourceRevision"] != plan["sourceRevision"] or receipt["version"] != plan["version"]
                or receipt["product"] != product or receipt["identity"] != expected):
            raise ReleaseError("Package provenance does not match the release plan.")
        filename = f"TypeWhisper-{product}-{rid}-{plan['version']}.msix"
        if receipt["package"] != filename:
            raise ReleaseError("Unexpected package filename.")
        package = receipt_path.parent / filename
        with package.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != receipt["sha256"]:
            raise ReleaseError("MSIX hash does not match its receipt.")
        with zipfile.ZipFile(package) as archive:
            if not {"TypeWhisper.exe", "TypeWhisper.dll", "TypeWhisper.pri"}.issubset(archive.namelist()):
                raise ReleaseError("MSIX is missing required application payloads.")
            manifest = ET.fromstring(archive.read("AppxManifest.xml"))
            identity = manifest.find("{*}Identity")
            expected_manifest = {"Name": expected["packageIdentityName"], "Publisher": expected["packagePublisher"],
                                 "Version": plan["version"], "ProcessorArchitecture": rid.removeprefix("win-")}
            if identity is None or any(identity.get(key) != value for key, value in expected_manifest.items()):
                raise ReleaseError("MSIX manifest does not match the Store destination.")
        result.setdefault(destination, []).append(package)
    if set(receipts) != {(destination, rid) for destination in plan["destinations"] for rid in ("win-x64", "win-arm64")}:
        raise ReleaseError("Both x64 and ARM64 packages are required for every destination.")
    return result


def upload_archive(url, archive):
    parsed = urlsplit(url)
    if parsed.scheme != "https" or not parsed.hostname or not parsed.hostname.endswith(".blob.core.windows.net") or parsed.port not in (None, 443):
        raise ReleaseError("Store returned an unexpected upload host.")
    connection = http.client.HTTPSConnection(parsed.hostname, timeout=600)
    try:
        with Path(archive).open("rb") as stream:
            connection.request("PUT", parsed.path + "?" + parsed.query, stream,
                               {"x-ms-blob-type": "BlockBlob", "x-ms-version": "2021-12-02",
                                "Content-Type": "application/zip", "Content-Length": str(Path(archive).stat().st_size)})
            response = connection.getresponse()
            response.read()
            if response.status != 201:
                raise ReleaseError(f"Store package upload returned HTTP {response.status}.")
    except (OSError, http.client.HTTPException):
        raise ReleaseError("Store package upload failed; the pending submission was left intact for inspection.") from None
    finally:
        connection.close()


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def submit(store, github, plan, identities, package_dir, receipt_path, upload=upload_archive, sleep=time.sleep):
    if not plan.get("publish"):
        raise ReleaseError("Release plan has no submissions to publish.")
    packages = validate_packages(package_dir, plan, identities)
    # The Store may have changed during the builds. Revalidate before the first write.
    current = make_plan(store, github, plan["dailyRunId"], identities, plan["releaseLine"])
    if current != plan:
        raise ReleaseError("Store or Daily state changed during packaging. Run again; no submission was created.")
    receipt = {"sourceRevision": plan["sourceRevision"], "version": plan["version"],
               "dailyRunId": plan["dailyRunId"], "submissions": []}
    write_json(receipt_path, receipt)
    for destination in plan["destinations"]:
        target = plan["targets"][destination]
        path = target["path"]
        product = identities[DESTINATIONS[destination]]["storeProductId"]
        expected_path = f"applications/{product}"
        if destination == "internal-flight":
            expected_path += "/flights/" + target["flightId"]
        if path != expected_path or (destination == "internal-flight" and "/flights/" not in path):
            raise ReleaseError("Refusing to submit to an unexpected Store destination.")
        # Guard again between destinations against a concurrent manual submission.
        live = store.call("GET", path)
        pending_key = "pendingFlightSubmission" if destination == "internal-flight" else "pendingApplicationSubmission"
        published_key = "lastPublishedFlightSubmission" if destination == "internal-flight" else "lastPublishedApplicationSubmission"
        if live.get(pending_key) or live.get(published_key, {}).get("id") != target["lastPublishedId"]:
            raise ReleaseError(f"{destination} changed before submission; existing drafts were preserved.")
        draft = store.call("POST", path + "/submissions")
        submission_path = path + "/submissions/" + draft["id"]
        entry = {"destination": destination, "productId": product, "submissionId": draft["id"],
                 "status": "DraftCreated", "packages": [p.name for p in packages[destination]]}
        receipt["submissions"].append(entry)
        write_json(receipt_path, receipt)
        payload = copy.deepcopy(draft)
        key = "flightPackages" if destination == "internal-flight" else "applicationPackages"
        for package in payload.get(key, []):
            package["fileStatus"] = "PendingDelete"
        payload.setdefault(key, []).extend({"fileName": package.name, "fileStatus": "PendingUpload",
                                           "minimumDirectXVersion": "None", "minimumSystemRam": "None"}
                                          for package in packages[destination])
        payload["targetPublishMode"] = "Immediate"
        payload["targetPublishDate"] = ""
        payload["notesForCertification"] = certification_notes(
            draft.get("notesForCertification", ""), plan["sourceRevision"], plan["version"], plan["dailyRunId"])
        # Listings, IARC ratings, pricing, visibility and audiences remain inherited.
        store.call("PUT", submission_path, payload)
        with tempfile.TemporaryDirectory(prefix="store-beta-") as temporary:
            archive = Path(temporary) / "packages.zip"
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as output:
                for package in packages[destination]:
                    output.write(package, package.name)
            upload(draft["fileUploadUrl"], archive)
        entry["status"] = "CommitRequested"
        write_json(receipt_path, receipt)
        store.call("POST", submission_path + "/commit")
        for attempt in range(19):
            status = store.call("GET", submission_path + "/status")["status"]
            entry["status"] = status
            write_json(receipt_path, receipt)
            if status in FAILURES:
                raise ReleaseError(f"{destination} submission {draft['id']} failed: {status}. Review Partner Center.")
            if status in ACTIVE - {"CommitStarted"} or status == "Published":
                break
            if status not in ("CommitStarted", "PendingCommit"):
                raise ReleaseError(f"Unexpected submission status: {status}.")
            if attempt == 18:
                raise ReleaseError(f"{destination} commit is not confirmed yet; inspect submission {draft['id']} before retrying.")
            sleep(10)
        print(f"{destination}: submission {draft['id']} is {entry['status']}.")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    plan_args = commands.add_parser("plan")
    plan_args.add_argument("--daily-run", required=True)
    plan_args.add_argument("--release-line", default="1.1")
    plan_args.add_argument("--output", default="artifacts/store-beta/plan.json")
    submit_args = commands.add_parser("submit")
    submit_args.add_argument("--plan", required=True)
    submit_args.add_argument("--packages", required=True)
    submit_args.add_argument("--receipt", default="artifacts/store-beta/submission-receipt.json")
    args = parser.parse_args()
    store, github, identities = Store(), GitHub(), products()
    if args.command == "plan":
        plan = make_plan(store, github, args.daily_run, identities, args.release_line)
        write_json(args.output, plan)
        print(plan["reason"])
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write(f"publish={str(plan['publish']).lower()}\n")
                output.write(f"source={plan['sourceRevision'] or ''}\nversion={plan.get('version', '')}\n")
                output.write("destinations=" + json.dumps(plan["destinations"]) + "\n")
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write("## Store beta plan\n\n" + plan["reason"] + "\n\n")
                summary.write(f"- Daily run: {plan['dailyRunId']}\n- Source: {plan['sourceRevision']}\n")
                if plan["publish"]:
                    summary.write(f"- Version: {plan['version']}\n- Destinations: {', '.join(plan['destinations'])}\n")
    else:
        plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        receipt = submit(store, github, plan, identities, args.packages, args.receipt)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
                summary.write("## Store beta submissions\n\n")
                for submission in receipt["submissions"]:
                    summary.write(f"- {submission['destination']}: {submission['submissionId']} ({submission['status']})\n")
                summary.write("\nMicrosoft certification and Store delivery still apply. This run does not establish installed-package or native ARM64 acceptance.\n")


if __name__ == "__main__":
    try:
        main()
    except ReleaseError as error:
        print(f"Store beta release: {error}", file=sys.stderr)
        sys.exit(1)
