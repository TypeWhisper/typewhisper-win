"""Downloads and verifies the diarization-v1 audio set.

    python -I download_set.py <data-dir> [--jobs 8] [--verify-only]

Reads <data-dir>/diarization-set.json (fetched from the benchmark repository when missing),
downloads every item's audio to <data-dir>/audio/<item.audio> and checks size and SHA-256.
The AMI mirror serves about 200 KB/s per connection and refuses range requests, so files load
in parallel and a broken file starts over. The synthetic conversations come from one release zip.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
import time
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

SET_URL = (
    "https://raw.githubusercontent.com/TypeWhisper/typewhisper-benchmark/"
    "seofood/diarization-benchmark/bench/diarization/diarization-set.json"
)
SYNTHETIC_ZIP = (
    "https://github.com/TypeWhisper/typewhisper-benchmark/releases/download/"
    "diarization-set-v1/diarization-synthetic-v1.zip"
)
USER_AGENT = "typewhisper-diarization-benchmark"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def matches(path: Path, item: dict) -> bool:
    return path.is_file() and path.stat().st_size == item["bytes"] and sha256(path) == item["sha256"]


def fetch(url: str, target: Path, attempts: int = 6) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    part = target.with_suffix(target.suffix + ".part")
    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=120) as response, part.open("wb") as out:
                shutil.copyfileobj(response, out, 1 << 20)
            part.replace(target)
            return
        except Exception as error:  # noqa: BLE001 - any failure restarts the file from the beginning
            print(f"  retry {attempt}/{attempts} {target.name}: {error}", flush=True)
            part.unlink(missing_ok=True)
            time.sleep(min(60, 5 * attempt))
    raise RuntimeError(f"giving up on {url}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("data_dir", type=Path)
    parser.add_argument("--jobs", type=int, default=8)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    data: Path = args.data_dir
    audio = data / "audio"
    manifest = data / "diarization-set.json"
    if not manifest.exists():
        fetch(SET_URL, manifest)
    items = json.loads(manifest.read_text(encoding="utf-8"))["items"]

    if not args.verify_only:
        synthetic = [i for i in items if i["group"] == "synthetic"]
        if any(not matches(audio / i["audio"], i) for i in synthetic):
            print("synthetic conversations", flush=True)
            with tempfile.TemporaryDirectory(dir=data) as tmp:
                zip_path = Path(tmp) / "synthetic.zip"
                fetch(SYNTHETIC_ZIP, zip_path)
                with zipfile.ZipFile(zip_path) as z:
                    for name in z.namelist():
                        if name.endswith(".wav") and ".." not in name:
                            target = audio / "synthetic" / Path(name).name
                            target.parent.mkdir(parents=True, exist_ok=True)
                            with z.open(name) as src, target.open("wb") as out:
                                shutil.copyfileobj(src, out)

        todo = [i for i in items if i["group"] != "synthetic" and not matches(audio / i["audio"], i)]
        print(f"{len(todo)} AMI files to download", flush=True)
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            futures = {pool.submit(fetch, i["url"], audio / i["audio"]): i for i in todo}
            for future in as_completed(futures):
                item = futures[future]
                try:
                    future.result()
                    ok = matches(audio / item["audio"], item)
                    print(f"  {item['audio']}: {'ok' if ok else 'CHECKSUM MISMATCH'}", flush=True)
                except Exception as error:  # noqa: BLE001
                    print(f"  {item['audio']}: FAILED {error}", flush=True)

    bad = [i["audio"] for i in items if not matches(audio / i["audio"], i)]
    for name in bad:
        print(f"bad or missing: {name}", flush=True)
    print(f"{len(items) - len(bad)}/{len(items)} files verified", flush=True)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
