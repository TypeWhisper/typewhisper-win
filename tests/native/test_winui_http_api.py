"""Real WinUI HTTP acceptance; requires an isolated profile and ready LOCAL model."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import threading
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--profile", type=Path, required=True)
parser.add_argument("--audio", type=Path, required=True, help="Synthetic test speech WAV")
args = parser.parse_args()
discovery = json.loads((args.profile / "api-discovery.json").read_text(encoding="utf-8"))
assert discovery["version"] == 1
assert int((args.profile / "api-port").read_text()) == discovery["port"]
base = "http://127.0.0.1:" + str(discovery["port"])
token = discovery["token"]
assert len(token) >= 32
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def request(route, method="GET", data=None, media=None, auth=True, extra=None):
    headers = {"Authorization": "Bearer " + token} if auth else {}
    if media:
        headers["Content-Type"] = media
    headers.update(extra or {})
    req = urllib.request.Request(base + route, data=data, method=method, headers=headers)
    try:
        with opener.open(req, timeout=120) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()

def fingerprint(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None

assert request("/v1/status", auth=False)[0] == 200
assert request("/v1/models", auth=False)[0] == 401
assert request("/v1/models", auth=False, extra={"Authorization": "Bearer incorrect"})[0] == 401
assert request("/v1/models", extra={"Origin": "https://example.invalid"})[0] == 403
code, body = request("/v1/models")
models = json.loads(body)
assert code == 200 and models["status"] == "ready"
active = [model for model in models["models"] if model["active"]]
assert len(active) == 1 and not active[0]["cloud"], "Use a LOCAL test model."
assert request("/v1/models", "POST", b"")[0] == 405
assert request("/v1/unknown")[0] == 404
assert request("/v1/transcribe?language=en&language=de", "POST", b"x", "audio/wav")[0] == 400
assert request("/v1/transcribe", "POST", b"not audio", "audio/wav")[0] == 422
audio = args.audio.read_bytes()
before = fingerprint(args.audio)
history_before = fingerprint(args.profile / "history.json")
assert request("/v1/transcribe?prompt=unsupported", "POST", audio, "audio/wav")[0] == 400
assert request("/v1/transcribe?model=not-selected", "POST", audio, "audio/wav")[0] == 409
code, body = request("/v1/transcribe?task=transcribe&response_format=json", "POST", audio, "audio/wav")
assert code == 200, (code, body.decode("utf-8"))
result = json.loads(body)
assert result["text"].strip() and result["duration"] > 0
assert result["model"] == models["model"]
assert all(set(segment) == {"text", "start", "end"} for segment in result["segments"])
assert request("/v1/transcribe?language=EN&task=transcribe", "POST", audio, "audio/wav")[0] == 200
code, local_body = request("/v1/transcribe/local-file", "POST",
    json.dumps({"path": str(args.audio.resolve()), "task": "transcribe", "response_format": "text"}).encode(), "application/json")
assert code == 200 and local_body.decode().strip() == result["text"].strip()
boundary = "TypeWhisperNativeSmokeBoundary"
multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="speech.wav"\r\n'
             'Content-Type: audio/wav\r\n\r\n').encode() + audio + (f'\r\n--{boundary}--\r\n').encode()
code, multipart_body = request("/v1/transcribe?task=transcribe", "POST", multipart, "multipart/form-data; boundary=" + boundary)
assert code == 200 and json.loads(multipart_body)["text"].strip() == result["text"].strip()
barrier = threading.Barrier(2)
def simultaneous(_):
    barrier.wait()
    return request("/v1/transcribe?task=transcribe", "POST", audio, "audio/wav")[0]
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    statuses = list(pool.map(simultaneous, range(2)))
assert sorted(statuses) == [200, 409], statuses
assert fingerprint(args.audio) == before
assert fingerprint(args.profile / "history.json") == history_before
assert not list((args.profile / "HttpApi" / "Uploads").glob("*"))
assert request("/v1/models")[0] == 200
print(json.dumps({"passed": True, "model": models["model"], "duration": result["duration"],
    "concurrent_statuses": sorted(statuses), "history_unchanged": True, "source_unchanged": True,
    "upload_directory_empty": True}, indent=2))
