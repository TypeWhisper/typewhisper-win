"""Verify a changed backup round trip and concurrent import rejection in the isolated parity profile."""
import concurrent.futures
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.request

profile = Path(os.environ["TEMP"]) / "TypeWhisper-WinUI-TestProfiles/http-api-parity-20260908"
discovery = json.loads((profile / "api-discovery.json").read_text(encoding="utf-8"))
assert discovery["requires_authentication"] is False
base = "http://127.0.0.1:" + str(discovery["port"])
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def request(path, method="GET", body=None):
    data = json.dumps(body).encode() if body is not None else None
    try:
        with opener.open(urllib.request.Request(base + path, data=data, method=method,
                headers={"Content-Type": "application/json"}), timeout=20) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)

term = "HttpApiParityBackupRoundTrip"
assert request("/v1/dictionary/terms", "PUT", {"terms": [term]})[0] == 200
code, backup = request("/v1/settings/export")
assert code == 200
assert request("/v1/dictionary/terms", "DELETE", {"term": term})[0] == 200
with concurrent.futures.ThreadPoolExecutor(2) as pool:
    results = list(pool.map(lambda _: request("/v1/settings/import", "POST", backup), range(2)))
statuses = sorted(result[0] for result in results)
assert statuses in ([202, 409], [202, 503]), statuses
restored = False
for _ in range(100):
    time.sleep(.2)
    try:
        code, terms = request("/v1/dictionary/terms")
        if code == 200 and term in terms.get("terms", []):
            restored = True
            break
    except (urllib.error.URLError, TimeoutError, ConnectionError):
        pass
assert restored, "Restart did not restore the imported fixture"
assert request("/v1/dictionary/terms", "DELETE", {"term": term})[0] == 200
evidence = {"passed": True, "import_statuses": statuses, "restored_after_restart": True}
Path("artifacts/http-api-backup-native-evidence.json").write_text(json.dumps(evidence), encoding="utf-8")
print(json.dumps(evidence))
