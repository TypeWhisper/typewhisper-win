"""Native API acceptance. Prepare offline, then launch the isolated profile separately.

Requires a ready local Canary model. Never launches an app, downloads/deletes model
assets, imports a valid backup, or stores API response bodies in the evidence file.
The short dictation can insert recognized audio into the foreground application;
the caller must leave a harmless target active before running this script.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave


PROFILE_NAME = "http-api-parity-20260908"
PREFIX = "HttpApiParity20260908"
HISTORY_ID = "611e9625-773b-450a-a0cf-a4af0175e57b"
KEEP_ID = "d7cec676-f4fc-43c1-aa0e-b34fa1c2926e"
WORKFLOW_ID = "e2bc2f34-5c86-4634-ab62-cd0d2548ed87"
ROUTES = [
    ("POST", "/v1/transcribe"), ("POST", "/v1/transcribe/local-file"),
    ("GET", "/v1/status"), ("GET", "/v1/models"),
    ("POST", "/v1/models/load"), ("POST", "/v1/models/unload"), ("DELETE", "/v1/models"),
    ("GET", "/v1/history"), ("DELETE", "/v1/history"),
    ("GET", "/v1/rules"), ("PUT", "/v1/rules/toggle"),
    ("GET", "/v1/profiles"), ("PUT", "/v1/profiles/toggle"),
    ("POST", "/v1/dictation/start"), ("POST", "/v1/dictation/stop"),
    ("GET", "/v1/dictation/status"), ("GET", "/v1/dictation/transcription"),
    ("POST", "/v1/recorder/start"), ("POST", "/v1/recorder/stop"),
    ("GET", "/v1/recorder/status"), ("GET", "/v1/recorder/session"),
    ("GET", "/v1/dictionary/terms"), ("PUT", "/v1/dictionary/terms"), ("DELETE", "/v1/dictionary/terms"),
    ("GET", "/v1/dictionary/corrections"), ("PUT", "/v1/dictionary/corrections"), ("DELETE", "/v1/dictionary/corrections"),
    ("GET", "/v1/settings/export"), ("POST", "/v1/settings/import"),
]


def write_json(path, value):
    temporary = path.with_name(path.name + ".parity.tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)


def prepare(profile):
    if (profile / "api-discovery.json").exists():
        raise RuntimeError("Prepare requires the app to be stopped and discovery removed.")
    profile.mkdir(parents=True, exist_ok=True)
    now = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    for filename, fixtures in [
        ("history.json", [dict(Id=entry_id, Timestamp=now, CreatedAt=now,
            RawText=PREFIX + " raw fixture " + suffix, FinalText=PREFIX + " final fixture " + suffix,
            AppName=PREFIX, DurationSeconds=1.5, Language="en", EngineUsed="sherpa-onnx", ModelUsed="canary-180m")
            for entry_id, suffix in [(HISTORY_ID, "delete"), (KEEP_ID, "retain")]]),
        ("workflows.json", [dict(Id=WORKFLOW_ID, Name=PREFIX + " Workflow", IsEnabled=False,
            SortOrder=9900, Template="Custom", Trigger={"Kind": "Manual"},
            Behavior={"ProviderOverride": "none"}, CreatedAt=now, UpdatedAt=now)]),
    ]:
        path = profile / filename
        existing = json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else []
        if not isinstance(existing, list):
            raise RuntimeError("Fixture preparation requires a JSON array.")
        own_ids = {item["Id"] for item in fixtures}
        for item in existing:
            if item.get("Id") in own_ids and not (item.get("Name", "").startswith(PREFIX) or item.get("AppName") == PREFIX):
                raise RuntimeError("Fixture identity collision; refusing to replace unrelated data.")
        write_json(path, [item for item in existing if item.get("Id") not in own_ids] + fixtures)
    print(json.dumps({"prepared": True, "history_fixtures": 2, "workflow_fixtures": 1}))


class Acceptance:
    def __init__(self, profile, evidence):
        self.profile, self.evidence = profile, evidence
        self.checks, self.http = [], []
        discovery = json.loads((profile / "api-discovery.json").read_text(encoding="utf-8-sig"))
        self.check(discovery.get("requires_authentication") is False, "discovery unauthenticated mode")
        port = discovery["port"]
        self.check(isinstance(port, int) and 1 <= port <= 65535, "discovery valid port")
        self.check(int((profile / "api-port").read_text()) == port, "discovery port marker")
        self.base = "http://127.0.0.1:" + str(port)
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def check(self, condition, label):
        self.checks.append({"check": label, "passed": bool(condition)})
        if not condition:
            raise AssertionError(label)

    def request(self, path, method="GET", payload=None, query=None, expected=None):
        url = self.base + path + ("?" + urllib.parse.urlencode(query) if query else "")
        data = json.dumps(payload).encode() if payload is not None else None
        request = urllib.request.Request(url, data=data, method=method,
            headers={"Content-Type": "application/json"} if data is not None else {})
        try:
            with self.opener.open(request, timeout=120) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        self.http.append({"method": method, "path": path, "status": status})
        if expected is not None:
            self.check(status in expected, method + " " + path + " expected status")
        try:
            value = json.loads(body)
        except (ValueError, UnicodeDecodeError):
            value = None
        return status, value, body

    def json(self, path, method="GET", payload=None, query=None, expected=(200,)):
        _, value, _ = self.request(path, method, payload, query, expected)
        self.check(isinstance(value, dict), method + " " + path + " JSON object")
        return value

    def poll(self, path, session_id):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            value = self.json(path, query={"id": session_id})
            if value["status"] in ("completed", "failed"):
                return value
            self.check(value["status"] in ("recording", "processing", "finalizing"), "session has known intermediate status")
            time.sleep(0.35)
        raise AssertionError("Session polling timed out")

    def matrix(self):
        self.check(len(ROUTES) == 29, "29 Mac method/path pairs")
        for method, path in ROUTES:
            query, payload = None, None
            if path == "/v1/dictation/start":
                payload = {"unexpected": True}
            elif path == "/v1/recorder/start":
                query = {"mic": "false", "system_audio": "false"}
            elif method in ("PUT", "POST", "DELETE") and path not in ("/v1/history", "/v1/models"):
                payload = {}
            status, _, _ = self.request(path, method, payload, query)
            self.check(status in (200, 400, 409, 415, 422), method + " " + path + " implemented")
        for path in sorted({path for _, path in ROUTES}):
            self.request(path, "PATCH", expected=(405,))
        self.request("/v1/parity-unknown-route", expected=(404,))

    def history(self):
        page = self.json("/v1/history", query={"q": PREFIX, "limit": "1", "offset": "0"})
        self.check(page["total"] == 2 and page["limit"] == 1 and len(page["entries"]) == 1, "history filtered pagination")
        entry = page["entries"][0]
        fields = {"id", "text", "raw_text", "timestamp", "app_name", "app_bundle_id", "app_url", "duration", "language", "engine", "model", "words_count"}
        self.check(fields <= set(entry), "history Raycast fields")
        self.check(entry["app_name"] == PREFIX and entry["text"].startswith(PREFIX), "history own fixture identity")
        deleted = self.json("/v1/history", "DELETE", query={"id": HISTORY_ID})
        self.check(deleted["deleted"] is True, "history fixture deletion")
        remaining = self.json("/v1/history", query={"q": PREFIX})
        self.check(remaining["total"] == 1 and remaining["entries"][0]["id"] == KEEP_ID, "history deletion preserves other fixture")
        self.request("/v1/history", "DELETE", query={"id": HISTORY_ID}, expected=(404,))

    def workflows(self):
        profiles = self.json("/v1/profiles")["profiles"]
        fixture = next((item for item in profiles if item["id"] == WORKFLOW_ID), None)
        self.check(fixture is not None and fixture["name"].startswith(PREFIX), "workflow own fixture")
        original = fixture["is_enabled"]
        try:
            toggled = self.json("/v1/profiles/toggle", "PUT", query={"id": WORKFLOW_ID})
            self.check(toggled["is_enabled"] is not original, "profile toggle persisted")
            rules = self.json("/v1/rules")["rules"]
            self.check(next(item for item in rules if item["id"] == WORKFLOW_ID)["is_enabled"] is not original, "rules alias sees profile toggle")
        finally:
            current = next(item for item in self.json("/v1/profiles")["profiles"] if item["id"] == WORKFLOW_ID)
            if current["is_enabled"] != original:
                restored = self.json("/v1/rules/toggle", "PUT", query={"id": WORKFLOW_ID})
                self.check(restored["is_enabled"] == original, "workflow toggle restored")

    def dictionary(self):
        term, original = PREFIX + "Term" + uuid.uuid4().hex[:8], PREFIX + "Original" + uuid.uuid4().hex[:8]
        before_terms = self.json("/v1/dictionary/terms")["terms"]
        before_corrections = self.json("/v1/dictionary/corrections")["corrections"]
        try:
            added = self.json("/v1/dictionary/terms", "PUT", {"term_entries": [{"term": term, "ctc_min_similarity": 0.8}]})
            self.check(term in added["terms"], "dictionary adds owned term")
            self.check(next(item for item in added["term_entries"] if item["term"] == term)["ctc_min_similarity"] == 0.8, "term threshold persisted")
            replacement = PREFIX + "Replacement"
            added = self.json("/v1/dictionary/corrections", "PUT", {"original": original, "replacement": replacement, "caseSensitive": True})
            item = next(item for item in added["corrections"] if item["original"] == original)
            self.check(item["replacement"] == replacement and item["caseSensitive"] is True, "dictionary correction persisted")
        finally:
            self.json("/v1/dictionary/terms", "DELETE", {"term": term})
            self.json("/v1/dictionary/corrections", "DELETE", {"original": original})
        self.check(self.json("/v1/dictionary/terms")["terms"] == before_terms, "dictionary unrelated terms preserved")
        self.check(self.json("/v1/dictionary/corrections")["corrections"] == before_corrections, "dictionary unrelated corrections preserved")

    def models(self):
        catalog = self.json("/v1/models")
        active = [item for item in catalog["models"] if item["active"]]
        self.check(len(active) == 1 and active[0]["cloud"] is False, "active local model required")
        model = active[0]
        self.check("canary" in model["id"].lower(), "active Canary fixture required")
        self.check({"selected", "loaded", "downloaded", "size_description", "language_count", "engine"} <= set(model), "models Mac fields")
        self.engine, self.model_id = model["engine"], model["id"]
        try:
            unloaded = self.json("/v1/models/unload", "POST", {"engine": self.engine})
            self.check(unloaded["status"] == "unloaded", "local model unload")
            snapshot = self.json("/v1/models")
            matching = next(item for item in snapshot["models"] if item["engine"] == self.engine and item["id"] == self.model_id)
            self.check(matching["downloaded"] is True and matching["loaded"] is False, "unload retains downloaded assets")
        finally:
            loaded = self.json("/v1/models/load", "POST", {"engine": self.engine, "model": self.model_id})
            self.check(loaded["status"] == "ready", "local model load restores selection")
        self.request("/v1/models", "DELETE", query={"engine": self.engine, "model": PREFIX + "MissingModel"}, expected=(404,))

    def recorder(self):
        existing_outputs = {path.resolve() for path in (self.profile / "recordings").glob("*.wav")}
        self.check(self.json("/v1/recorder/status")["recording"] is False, "recorder initially idle")
        started = self.json("/v1/recorder/start", "POST", query={"mic": "true", "system_audio": "false"})
        session_id = str(uuid.UUID(started["id"]))
        try:
            self.check(started["status"] == "recording", "recorder start")
            self.check(self.json("/v1/recorder/status")["recording"] is True, "recorder active status")
            time.sleep(0.8)
        finally:
            stopped = self.json("/v1/recorder/stop", "POST")
            self.check(stopped["id"] == session_id, "recorder stop preserves session identity")
        completed = self.poll("/v1/recorder/session", session_id)
        self.check(completed["status"] == "completed", "recorder saved successfully")
        output = Path(completed["output_file"]).resolve()
        self.check(output.is_relative_to((self.profile / "recordings").resolve()) and output.suffix.lower() == ".wav", "recorder output is isolated WAV")
        self.check(output not in existing_outputs, "recorder output belongs to this test session")
        try:
            with wave.open(str(output), "rb") as recording:
                self.check(recording.getnframes() > 0 and recording.getframerate() > 0, "recorder output contains captured samples")
        finally:
            output.unlink(missing_ok=True)
        self.check(not output.exists(), "own recorder output removed")

    def dictation(self):
        self.check(self.json("/v1/dictation/status")["is_recording"] is False, "dictation initially idle")
        started = self.json("/v1/dictation/start", "POST")
        session_id = str(uuid.UUID(started["id"]))
        try:
            self.check(started["status"] == "recording", "dictation start")
            self.check(self.json("/v1/dictation/status")["is_recording"] is True, "dictation active status")
            time.sleep(0.25)
        finally:
            stopped = self.json("/v1/dictation/stop", "POST")
            self.check(stopped["id"] == session_id and stopped["status"] == "stopped", "dictation stop preserves session identity")
        completed = self.poll("/v1/dictation/transcription", session_id)
        self.check(completed["id"] == session_id and completed["status"] in ("completed", "failed"), "dictation reaches terminal session without requiring speech")
        self.check(self.json("/v1/dictation/status")["is_recording"] is False, "dictation idle after stop")

    def run(self):
        status = self.json("/v1/status")
        self.check(status["status"] in ("ok", "ready"), "status health")
        self.check({"engine", "model", "supports_streaming", "supports_translation"} <= set(status), "status Raycast fields")
        self.json("/v1/capabilities")
        for path in ("/docs", "/docs/"):
            _, _, body = self.request(path, expected=(200,))
            self.check(b"/v1/history" in body and b"/v1/models" in body, "documentation endpoint examples")
        self.matrix()
        self.history()
        self.workflows()
        self.dictionary()
        self.models()
        self.recorder()
        self.dictation()
        exported = self.json("/v1/settings/export")
        envelope = {key.lower().replace("_", ""): value for key, value in exported.items()}
        self.check(envelope.get("format") == "typewhisper-backup" and envelope.get("schemaversion") == 1
            and isinstance(envelope.get("data"), dict), "settings export produces valid backup envelope")
        self.request("/v1/settings/import", "POST", {}, expected=(400,))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--prepare", action="store_true", help="Add only owned fixtures offline; caller configures the ready local model and no-token API.")
    parser.add_argument("--evidence", type=Path, help="Sanitized JSON evidence destination; defaults to the isolated profile.")
    args = parser.parse_args()
    profile = args.profile.resolve()
    if profile.name != PROFILE_NAME:
        parser.error("--profile must resolve to the isolated " + PROFILE_NAME + " directory")
    if args.prepare:
        prepare(profile)
        return 0
    evidence = args.evidence or profile / "api-parity-evidence.json"
    acceptance = None
    result = {"passed": False, "mac_route_count": 29, "checks": [], "http": []}
    try:
        acceptance = Acceptance(profile, evidence)
        acceptance.run()
        result["passed"] = True
    except Exception as error:
        result["failure_type"] = type(error).__name__
        # Assertion messages originate here; never record remote bodies, transcripts or credentials.
        if isinstance(error, AssertionError):
            result["failed_check"] = str(error)
    finally:
        if acceptance is not None:
            result["checks"], result["http"] = acceptance.checks, acceptance.http
        evidence.parent.mkdir(parents=True, exist_ok=True)
        write_json(evidence, result)
        print(json.dumps(result, indent=2))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
