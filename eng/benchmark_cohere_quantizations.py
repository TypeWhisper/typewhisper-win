#!/usr/bin/env python3
"""Benchmark the local Cohere Transcribe GGUF variants on Windows.

The runner downloads a pinned subset of the public FLEURS test corpus, prepares
the selected models through TypeWhisper's local API, then starts one persistent
CrispASR process per quantization. It records accuracy, throughput, model load
time, peak CrispASR working set, and silence/noise behavior. It intentionally
uses only the Python standard library.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import math
import os
import platform
import random
import re
import secrets
import socket
import statistics
import struct
import subprocess
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


SCRIPT_VERSION = 2
EUROPE_DATASET = (
    "FluidInference/fleurs",
    "8944693da251acbaf2f9686bddc4fedce8bd2edd",
)
FULL_DATASET = (
    "FluidInference/fleurs-full",
    "1cca811bb8ea4d370345f108f00518167040282c",
)
DEFAULT_MODELS = [
    "cohere-transcribe-03-2026-q4_k",
    "cohere-transcribe-03-2026-q5_0",
    "cohere-transcribe-03-2026-q6_k",
    "cohere-transcribe-03-2026-q8_0",
]
MODEL_FILES = {
    "cohere-transcribe-03-2026-q4_k": "cohere-transcribe-q4_k.gguf",
    "cohere-transcribe-03-2026-q5_0": "cohere-transcribe-q5_0.gguf",
    "cohere-transcribe-03-2026-q6_k": "cohere-transcribe-q6_k.gguf",
    "cohere-transcribe-03-2026-q8_0": "cohere-transcribe-q8_0.gguf",
}
CRISPASR_VERSION = "0.8.24"
BACKENDS = {
    "nvidia-cuda": ("cuda", "cuda", False),
    "amd-vulkan": ("vulkan", "vulkan", False),
    "cpu": ("cpu", "cpu", True),
}
VAD_MODEL_FILE = "ggml-silero-v6.2.0.bin"
LANGUAGE_ID_MODEL_FILE = "ecapa-lid-107-f16.gguf"
LANGUAGES = {
    "de_de": ("de", EUROPE_DATASET, "wer"),
    "en_us": ("en", EUROPE_DATASET, "wer"),
    "fr_fr": ("fr", EUROPE_DATASET, "wer"),
    "es_419": ("es", EUROPE_DATASET, "wer"),
    "it_it": ("it", EUROPE_DATASET, "wer"),
    "pt_br": ("pt", EUROPE_DATASET, "wer"),
    "el_gr": ("el", EUROPE_DATASET, "wer"),
    "nl_nl": ("nl", EUROPE_DATASET, "wer"),
    "pl_pl": ("pl", EUROPE_DATASET, "wer"),
    "ja_jp": ("ja", FULL_DATASET, "cer"),
    "cmn_hans_cn": ("zh", FULL_DATASET, "cer"),
    "ko_kr": ("ko", FULL_DATASET, "cer"),
    "vi_vn": ("vi", FULL_DATASET, "wer"),
    "ar_eg": ("ar", FULL_DATASET, "wer"),
}
DEFAULT_LANGUAGES = list(LANGUAGES)
CJK_LANGUAGES = {"ja_jp", "cmn_hans_cn", "ko_kr"}


@dataclass(frozen=True)
class Sample:
    language: str
    language_hint: str
    sample_id: str
    reference: str
    audio_path: Path
    duration_seconds: float


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Benchmark Cohere GGUF quantizations with one persistent CrispASR "
            "process per model."
        )
    )
    parser.add_argument("--api-url", default="http://127.0.0.1:8978")
    parser.add_argument(
        "--api-token",
        default=os.environ.get("TYPEWHISPER_API_TOKEN"),
        help="Optional local API bearer token (or TYPEWHISPER_API_TOKEN).",
    )
    parser.add_argument(
        "--hf-token",
        default=os.environ.get("HF_TOKEN"),
        help="Optional Hugging Face token for corpus downloads (or HF_TOKEN).",
    )
    parser.add_argument(
        "--models",
        default=",".join(DEFAULT_MODELS),
        help="Comma-separated Cohere model IDs.",
    )
    parser.add_argument(
        "--languages",
        default=",".join(DEFAULT_LANGUAGES),
        help="Comma-separated FLEURS language configurations.",
    )
    parser.add_argument("--samples", type=int, default=50)
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=Path("artifacts/cohere-quantization-benchmark/fleurs"),
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("artifacts/cohere-quantization-benchmark/direct-results.json"),
    )
    parser.add_argument(
        "--markdown-output",
        type=Path,
        default=Path("artifacts/cohere-quantization-benchmark/direct-results.md"),
    )
    parser.add_argument(
        "--asset-root",
        type=Path,
        help=(
            "Cohere plugin data root. Auto-detected from TypeWhisper Store, "
            "desktop, or development data when exactly one candidate exists."
        ),
    )
    parser.add_argument(
        "--backend",
        choices=["nvidia-cuda", "amd-vulkan", "cpu"],
        default="nvidia-cuda",
        help="CrispASR runtime and GPU backend to benchmark.",
    )
    parser.add_argument(
        "--threads",
        type=int,
        default=min(max((os.cpu_count() or 2) // 2, 1), 12),
        help="CrispASR inference threads (default matches the plugin heuristic).",
    )
    parser.add_argument(
        "--stop-typewhisper-sidecar",
        action="store_true",
        help=(
            "Stop a TypeWhisper-owned CrispASR process under the selected asset "
            "root before starting the direct benchmark."
        ),
    )
    parser.add_argument(
        "--no-prepare-models",
        action="store_true",
        help="Require every model to be downloaded instead of preparing it through the API.",
    )
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Continue an existing compatible result file.",
    )
    parser.add_argument("--checkpoint-every", type=int, default=10)
    parser.add_argument("--request-timeout", type=int, default=7_200)
    parser.add_argument("--startup-timeout", type=int, default=900)
    parser.add_argument("--download-retries", type=int, default=4)
    parser.add_argument("--download-workers", type=int, default=8)
    args = parser.parse_args()

    args.models = split_csv(args.models)
    args.languages = split_csv(args.languages)
    if not args.models:
        parser.error("At least one model is required.")
    if not args.languages:
        parser.error("At least one language is required.")
    unknown_languages = sorted(set(args.languages) - set(LANGUAGES))
    if unknown_languages:
        parser.error(f"Unsupported FLEURS languages: {', '.join(unknown_languages)}")
    if args.samples <= 0:
        parser.error("--samples must be positive.")
    if args.checkpoint_every <= 0:
        parser.error("--checkpoint-every must be positive.")
    if args.threads <= 0:
        parser.error("--threads must be positive.")
    if args.startup_timeout <= 0:
        parser.error("--startup-timeout must be positive.")
    if args.download_workers <= 0:
        parser.error("--download-workers must be positive.")
    return args


def split_csv(value: str) -> list[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def auth_headers(token: str | None) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"} if token else {}


def download_file(
    url: str,
    destination: Path,
    token: str | None,
    attempts: int,
) -> None:
    if destination.exists() and destination.stat().st_size > 0:
        return

    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".part")
    last_error: Exception | None = None

    for attempt in range(1, attempts + 1):
        try:
            request = urllib.request.Request(url, headers=auth_headers(token))
            with urllib.request.urlopen(request, timeout=120) as response:
                with temporary.open("wb") as output:
                    while chunk := response.read(1024 * 1024):
                        output.write(chunk)
            temporary.replace(destination)
            return
        except (OSError, urllib.error.URLError) as error:
            last_error = error
            if temporary.exists():
                temporary.unlink()
            if attempt < attempts:
                time.sleep(attempt)

    raise RuntimeError(f"Failed to download {url}") from last_error


def dataset_url(repository: str, revision: str, relative_path: str) -> str:
    escaped_path = "/".join(
        urllib.parse.quote(part, safe="") for part in relative_path.split("/")
    )
    return (
        f"https://huggingface.co/datasets/{repository}/resolve/"
        f"{revision}/{escaped_path}"
    )


def read_wav_duration(path: Path) -> float:
    with wave.open(str(path), "rb") as audio:
        return audio.getnframes() / audio.getframerate()


def prepare_samples(args: argparse.Namespace) -> list[Sample]:
    samples: list[Sample] = []
    for language in args.languages:
        language_hint, (repository, revision), _ = LANGUAGES[language]
        language_dir = args.cache_dir / repository.replace("/", "--") / revision / language
        transcript_path = language_dir / f"{language}.trans.txt"
        download_file(
            dataset_url(
                repository,
                revision,
                f"{language}/{language}.trans.txt",
            ),
            transcript_path,
            args.hf_token,
            args.download_retries,
        )

        transcript_entries = []
        for line in transcript_path.read_text(encoding="utf-8").splitlines():
            sample_id, separator, reference = line.partition(" ")
            if separator and reference.strip():
                transcript_entries.append((sample_id, reference.strip()))

        if len(transcript_entries) < args.samples:
            raise RuntimeError(
                f"{language} has only {len(transcript_entries)} transcript entries; "
                f"{args.samples} requested."
            )

        selected_entries = transcript_entries[: args.samples]

        def download_audio(entry: tuple[str, str]) -> None:
            sample_id, _ = entry
            audio_path = language_dir / f"{sample_id}.wav"
            download_file(
                dataset_url(
                    repository,
                    revision,
                    f"{language}/{sample_id}.wav",
                ),
                audio_path,
                args.hf_token,
                args.download_retries,
            )

        with concurrent.futures.ThreadPoolExecutor(
            max_workers=args.download_workers
        ) as executor:
            list(executor.map(download_audio, selected_entries))

        for sample_id, reference in selected_entries:
            audio_path = language_dir / f"{sample_id}.wav"
            samples.append(
                Sample(
                    language=language,
                    language_hint=language_hint,
                    sample_id=sample_id,
                    reference=reference,
                    audio_path=audio_path,
                    duration_seconds=read_wav_duration(audio_path),
                )
            )
    return samples


def sample_manifest_sha256(samples: Iterable[Sample]) -> str:
    digest = hashlib.sha256()
    for sample in samples:
        audio_hash = hashlib.sha256(sample.audio_path.read_bytes()).hexdigest()
        line = (
            f"{sample.language}\t{sample.sample_id}\t{audio_hash}\t"
            f"{sample.reference}\n"
        )
        digest.update(line.encode("utf-8"))
    return digest.hexdigest()


def api_request(
    args: argparse.Namespace,
    method: str,
    path: str,
    body: bytes | None = None,
    headers: dict[str, str] | None = None,
) -> Any:
    url = args.api_url.rstrip("/") + path
    request_headers = {
        "Accept": "application/json",
        **auth_headers(args.api_token),
        **(headers or {}),
    }
    request = urllib.request.Request(
        url,
        data=body,
        method=method,
        headers=request_headers,
    )
    try:
        with urllib.request.urlopen(
            request,
            timeout=args.request_timeout,
        ) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        response_body = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(
            f"TypeWhisper API returned HTTP {error.code}: {response_body}"
        ) from error


def multipart_body(
    fields: dict[str, str],
    file_name: str,
    file_data: bytes,
) -> tuple[bytes, str]:
    boundary = f"----typewhisper-benchmark-{uuid.uuid4().hex}"
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode(),
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode(),
                value.encode("utf-8"),
                b"\r\n",
            ]
        )
    chunks.extend(
        [
            f"--{boundary}\r\n".encode(),
            (
                'Content-Disposition: form-data; name="file"; '
                f'filename="{file_name}"\r\n'
            ).encode(),
            b"Content-Type: audio/wav\r\n\r\n",
            file_data,
            b"\r\n",
            f"--{boundary}--\r\n".encode(),
        ]
    )
    return b"".join(chunks), boundary


def typewhisper_transcribe(
    args: argparse.Namespace,
    model: str,
    sample: Sample,
    await_download: bool,
) -> tuple[dict[str, Any], float]:
    body, boundary = multipart_body(
        {
            "engine": "cohere-transcribe",
            "model": model,
            "language": sample.language_hint,
            "response_format": "verbose_json",
            "normalize_numbers": "false",
        },
        sample.audio_path.name,
        sample.audio_path.read_bytes(),
    )
    query = "?await_download=1" if await_download else ""
    started = time.perf_counter()
    response = api_request(
        args,
        "POST",
        f"/v1/transcribe{query}",
        body,
        {"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    wall_seconds = time.perf_counter() - started
    if response.get("model") != model:
        raise RuntimeError(
            f"Requested {model}, but TypeWhisper reported {response.get('model')}."
        )
    return response, wall_seconds


def normalize_text(text: str) -> str:
    normalized = unicodedata.normalize("NFKC", text).casefold()
    characters = []
    for character in normalized:
        category = unicodedata.category(character)
        characters.append(" " if category[0] in {"P", "S"} else character)
    return re.sub(r"\s+", " ", "".join(characters)).strip()


def edit_distance(reference: list[str], hypothesis: list[str]) -> int:
    previous = list(range(len(hypothesis) + 1))
    for row, reference_item in enumerate(reference, start=1):
        current = [row]
        for column, hypothesis_item in enumerate(hypothesis, start=1):
            current.append(
                min(
                    current[column - 1] + 1,
                    previous[column] + 1,
                    previous[column - 1]
                    + (0 if reference_item == hypothesis_item else 1),
                )
            )
        previous = current
    return previous[-1]


def score(reference: str, hypothesis: str) -> dict[str, int | str]:
    normalized_reference = normalize_text(reference)
    normalized_hypothesis = normalize_text(hypothesis)
    reference_words = normalized_reference.split()
    hypothesis_words = normalized_hypothesis.split()
    reference_characters = list(normalized_reference.replace(" ", ""))
    hypothesis_characters = list(normalized_hypothesis.replace(" ", ""))
    return {
        "normalized_reference": normalized_reference,
        "normalized_hypothesis": normalized_hypothesis,
        "word_edits": edit_distance(reference_words, hypothesis_words),
        "reference_words": len(reference_words),
        "character_edits": edit_distance(
            reference_characters,
            hypothesis_characters,
        ),
        "reference_characters": len(reference_characters),
    }


def make_negative_samples(directory: Path) -> list[Sample]:
    directory.mkdir(parents=True, exist_ok=True)
    definitions = [
        ("silence-1s", 1.0, 0.0),
        ("silence-5s", 5.0, 0.0),
        ("low-white-noise-5s", 5.0, 0.003),
    ]
    output = []
    for sample_id, duration, amplitude in definitions:
        path = directory / f"{sample_id}.wav"
        if not path.exists():
            random_source = random.Random(42)
            frame_count = round(16_000 * duration)
            with wave.open(str(path), "wb") as audio:
                audio.setnchannels(1)
                audio.setsampwidth(2)
                audio.setframerate(16_000)
                frames = bytearray()
                for _ in range(frame_count):
                    value = (
                        0
                        if amplitude == 0
                        else round(random_source.uniform(-amplitude, amplitude) * 32767)
                    )
                    frames.extend(struct.pack("<h", value))
                audio.writeframes(frames)
        output.append(
            Sample(
                language="negative",
                language_hint="de",
                sample_id=sample_id,
                reference="",
                audio_path=path,
                duration_seconds=duration,
            )
        )
    return output


def powershell_json(script: str) -> Any:
    try:
        completed = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                script,
            ],
            check=True,
            capture_output=True,
            text=True,
            timeout=30,
        )
        output = completed.stdout.strip()
        return json.loads(output) if output else None
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError):
        return None


def collect_hardware() -> dict[str, Any]:
    hardware: dict[str, Any] = {
        "platform": platform.platform(),
        "machine": platform.machine(),
        "processor": platform.processor(),
        "python": platform.python_version(),
    }
    if os.name == "nt":
        windows = powershell_json(
            """
$cpu = Get-CimInstance Win32_Processor |
  Select-Object Name,NumberOfCores,NumberOfLogicalProcessors
$gpu = Get-CimInstance Win32_VideoController |
  Select-Object Name,AdapterRAM,DriverVersion
$os = Get-CimInstance Win32_OperatingSystem |
  Select-Object Caption,Version,BuildNumber,TotalVisibleMemorySize
[pscustomobject]@{ cpu=$cpu; gpu=@($gpu); os=$os } |
  ConvertTo-Json -Compress -Depth 4
"""
        )
        if windows:
            hardware["windows"] = windows
        try:
            nvidia_smi = subprocess.run(
                [
                    "nvidia-smi",
                    "--query-gpu=name,memory.total,driver_version",
                    "--format=csv,noheader,nounits",
                ],
                check=True,
                capture_output=True,
                text=True,
                timeout=30,
            ).stdout.strip()
            if nvidia_smi:
                hardware["nvidia_smi"] = [
                    {
                        "name": parts[0],
                        "memory_total_mib": int(parts[1]),
                        "driver_version": parts[2],
                    }
                    for line in nvidia_smi.splitlines()
                    if len(parts := [part.strip() for part in line.split(",")]) == 3
                ]
        except (OSError, subprocess.SubprocessError, ValueError):
            # NVIDIA metadata is optional; the WMI hardware snapshot remains usable.
            pass
    return hardware


def crispasr_peak_working_set(process_id: int) -> int | None:
    if os.name != "nt":
        return None
    value = powershell_json(
        f"""
$process = Get-Process -Id {process_id} -ErrorAction SilentlyContinue
if ($process) {{
  [long]$process.PeakWorkingSet64 | ConvertTo-Json -Compress
}}
"""
    )
    return int(value) if value is not None else None


def is_same_or_under(path: Path, root: Path) -> bool:
    normalized_path = os.path.normcase(str(path.resolve()))
    normalized_root = os.path.normcase(str(root.resolve())).rstrip("\\/")
    return normalized_path == normalized_root or normalized_path.startswith(
        normalized_root + os.sep
    )


def runtime_executable(asset_root: Path, backend: str) -> Path | None:
    runtime_id = BACKENDS[backend][0]
    runtime_root = (
        asset_root / "Runtimes" / "CrispASR" / CRISPASR_VERSION / runtime_id
    )
    return next(runtime_root.rglob("crispasr.exe"), None) if runtime_root.is_dir() else None


def model_path(asset_root: Path, model: str) -> Path:
    try:
        file_name = MODEL_FILES[model]
    except KeyError as error:
        raise RuntimeError(f"Unknown Cohere benchmark model: {model}") from error
    return asset_root / "Models" / model / file_name


def auxiliary_paths(asset_root: Path) -> tuple[Path, Path, Path]:
    auxiliary_root = (
        asset_root
        / "Models"
        / "cohere-transcribe-03-2026-q5_0"
        / "Auxiliary"
    )
    return (
        auxiliary_root / LANGUAGE_ID_MODEL_FILE,
        auxiliary_root / VAD_MODEL_FILE,
        asset_root / "Cache" / "CrispASR",
    )


def asset_root_candidates() -> list[Path]:
    local_app_data_value = os.environ.get("LOCALAPPDATA")
    if not local_app_data_value:
        return []

    local_app_data = Path(local_app_data_value)
    candidates = [
        local_app_data
        / "TypeWhisper-UserData"
        / "PluginData"
        / "com.typewhisper.cohere-transcribe",
        local_app_data
        / "TypeWhisper-DevUserData"
        / "PluginData"
        / "com.typewhisper.cohere-transcribe",
    ]
    packages_root = local_app_data / "Packages"
    if packages_root.is_dir():
        candidates.extend(
            package
            / "LocalCache"
            / "Local"
            / "TypeWhisper-UserData"
            / "PluginData"
            / "com.typewhisper.cohere-transcribe"
            for package in packages_root.glob("TypeWhisper.TypeWhisper_*")
        )

    unique: dict[str, Path] = {}
    for candidate in candidates:
        if candidate.is_dir():
            unique[os.path.normcase(str(candidate.resolve()))] = candidate.resolve()
    return list(unique.values())


def validate_asset_root(
    asset_root: Path,
    models: list[str],
    backend: str,
) -> list[str]:
    missing = [
        str(path)
        for path in (model_path(asset_root, model) for model in models)
        if not path.is_file()
    ]
    language_id_path, vad_path, _ = auxiliary_paths(asset_root)
    missing.extend(
        str(path) for path in (language_id_path, vad_path) if not path.is_file()
    )
    if runtime_executable(asset_root, backend) is None:
        missing.append(
            str(
                asset_root
                / "Runtimes"
                / "CrispASR"
                / CRISPASR_VERSION
                / BACKENDS[backend][0]
                / "**"
                / "crispasr.exe"
            )
        )
    return missing


def discover_asset_root(
    requested_root: Path | None,
    models: list[str],
    backend: str,
) -> Path:
    if requested_root is not None:
        root = requested_root.expanduser().resolve()
        if not root.is_dir():
            raise RuntimeError(f"Cohere asset root does not exist: {root}")
        missing = validate_asset_root(root, models, backend)
        if missing:
            raise RuntimeError(
                "The selected Cohere asset root is incomplete:\n  "
                + "\n  ".join(missing)
            )
        return root

    candidates = [
        candidate
        for candidate in asset_root_candidates()
        if not validate_asset_root(candidate, models, backend)
    ]
    if len(candidates) == 1:
        return candidates[0]
    if not candidates:
        discovered = asset_root_candidates()
        details = "\n  ".join(str(path) for path in discovered) or "(none)"
        raise RuntimeError(
            "No complete Cohere asset root was found. Prepare every requested "
            f"model and the {backend} runtime in TypeWhisper, or pass "
            f"--asset-root explicitly. Discovered roots:\n  {details}"
        )
    raise RuntimeError(
        "Multiple complete Cohere asset roots were found. Pass --asset-root "
        "explicitly:\n  "
        + "\n  ".join(str(path) for path in candidates)
    )


def matching_crispasr_processes(asset_root: Path) -> list[dict[str, Any]]:
    if os.name != "nt":
        return []
    value = powershell_json(
        """
$processes = @(
  Get-CimInstance Win32_Process -Filter "Name='crispasr.exe'" |
    Select-Object ProcessId,ExecutablePath
)
ConvertTo-Json -InputObject $processes -Compress -Depth 3
"""
    )
    if not value:
        return []
    processes = value if isinstance(value, list) else [value]
    return [
        process
        for process in processes
        if process.get("ExecutablePath")
        and is_same_or_under(Path(process["ExecutablePath"]), asset_root)
    ]


def stop_typewhisper_sidecar(
    asset_root: Path,
    allow_stop: bool,
) -> list[int]:
    processes = matching_crispasr_processes(asset_root)
    if not processes:
        return []
    process_ids = sorted(int(process["ProcessId"]) for process in processes)
    if not allow_stop:
        raise RuntimeError(
            "TypeWhisper currently owns a CrispASR process under the selected "
            "asset root. Close TypeWhisper or pass --stop-typewhisper-sidecar; "
            "the TypeWhisper app itself remains open."
        )

    subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "Stop-Process -Id "
            + ",".join(str(process_id) for process_id in process_ids)
            + " -Force -ErrorAction Stop",
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=30,
    )
    deadline = time.monotonic() + 10
    while matching_crispasr_processes(asset_root):
        if time.monotonic() >= deadline:
            raise RuntimeError("The TypeWhisper CrispASR sidecar did not stop.")
        time.sleep(0.25)
    return process_ids


class CrispAsrProcess:
    def __init__(
        self,
        args: argparse.Namespace,
        model: str,
    ) -> None:
        self.args = args
        self.model = model
        self.api_key = secrets.token_hex(32)
        self.base_url: str | None = None
        self.process: subprocess.Popen[str] | None = None
        self.log_handle: Any = None
        self.log_path = args.output.parent / "logs" / f"{model}-{args.backend}.log"
        self.executable = runtime_executable(args.asset_root, args.backend)
        if self.executable is None:
            raise RuntimeError(
                f"The CrispASR {args.backend} runtime is missing under {args.asset_root}."
            )

    def start(self) -> dict[str, Any]:
        model_file = model_path(self.args.asset_root, self.model)
        language_id_file, vad_file, cache_directory = auxiliary_paths(
            self.args.asset_root
        )
        cache_directory.mkdir(parents=True, exist_ok=True)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_handle = self.log_path.open(
            "w",
            encoding="utf-8",
            errors="replace",
        )

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = int(reservation.getsockname()[1])
        self.base_url = f"http://127.0.0.1:{port}"

        _, gpu_backend, no_gpu = BACKENDS[self.args.backend]
        command = [
            str(self.executable),
            "--server",
            "--backend",
            "cohere",
            "--model",
            str(model_file),
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--language",
            "auto",
            "--lid-backend",
            "ecapa",
            "--lid-model",
            str(language_id_file),
            "--vad",
            "--vad-model",
            str(vad_file),
            "--strict-pipeline",
            "--require-vad",
            "--threads",
            str(self.args.threads),
            "--gpu-backend",
            gpu_backend,
        ]
        if no_gpu:
            command.append("--no-gpu")

        environment = os.environ.copy()
        environment["CRISPASR_API_KEYS"] = self.api_key
        environment["CRISPASR_CACHE_DIR"] = str(cache_directory)
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        started = time.perf_counter()
        self.process = subprocess.Popen(
            command,
            cwd=self.executable.parent,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=self.log_handle,
            stderr=subprocess.STDOUT,
            text=True,
            creationflags=creation_flags,
        )

        try:
            self._wait_until_ready()
        except Exception:
            exit_code = self.process.poll()
            log_tail = self._log_tail()
            self.stop()
            raise RuntimeError(
                f"CrispASR failed to start for {self.model}; "
                f"exit code {exit_code}. Log tail:\n{log_tail}"
            )
        return {
            "model": self.model,
            "process_id": self.process.pid,
            "executable": str(self.executable),
            "backend": self.args.backend,
            "threads": self.args.threads,
            "startup_seconds": time.perf_counter() - started,
            "log_path": str(self.log_path),
        }

    def _wait_until_ready(self) -> None:
        assert self.process is not None
        assert self.base_url is not None
        deadline = time.monotonic() + self.args.startup_timeout
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError("CrispASR exited during startup.")
            try:
                with urllib.request.urlopen(
                    self.base_url + "/health",
                    timeout=2,
                ) as response:
                    if response.status == 200:
                        return
            except (OSError, urllib.error.URLError):
                # Connection failures are expected until the server binds its port.
                pass
            time.sleep(0.25)
        raise TimeoutError(
            f"CrispASR did not become ready within {self.args.startup_timeout} seconds."
        )

    def transcribe(self, sample: Sample) -> tuple[dict[str, Any], float]:
        if self.process is None or self.process.poll() is not None:
            raise RuntimeError("CrispASR is not running.")
        assert self.base_url is not None
        fields = {
            "model": self.model,
            "response_format": "verbose_json",
        }
        if sample.language_hint != "auto":
            fields["language"] = sample.language_hint
        body, boundary = multipart_body(
            fields,
            sample.audio_path.name,
            sample.audio_path.read_bytes(),
        )
        request = urllib.request.Request(
            self.base_url + "/v1/audio/transcriptions",
            data=body,
            method="POST",
            headers={
                "Accept": "application/json",
                "Authorization": f"Bearer {self.api_key}",
                "Content-Type": f"multipart/form-data; boundary={boundary}",
            },
        )
        started = time.perf_counter()
        try:
            with urllib.request.urlopen(
                request,
                timeout=self.args.request_timeout,
            ) as response:
                result = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            response_body = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(
                f"CrispASR returned HTTP {error.code}: {response_body}"
            ) from error
        return result, time.perf_counter() - started

    def peak_working_set(self) -> int | None:
        if self.process is None:
            return None
        return crispasr_peak_working_set(self.process.pid)

    def _log_tail(self) -> str:
        if self.log_handle is not None:
            self.log_handle.flush()
        try:
            return "\n".join(
                self.log_path.read_text(
                    encoding="utf-8",
                    errors="replace",
                ).splitlines()[-80:]
            )
        except OSError:
            return "(log unavailable)"

    def stop(self) -> None:
        process = self.process
        self.process = None
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        if self.log_handle is not None:
            self.log_handle.close()
            self.log_handle = None


def git_revision() -> str | None:
    try:
        return subprocess.run(
            ["git", "rev-parse", "HEAD"],
            check=True,
            capture_output=True,
            text=True,
            timeout=10,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None


def atomic_write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    temporary.replace(path)


def result_key(run: dict[str, Any]) -> tuple[str, str, str]:
    return run["model"], run["language"], run["sample_id"]


def initial_result(
    args: argparse.Namespace,
    samples: list[Sample],
    manifest_hash: str,
) -> dict[str, Any]:
    return {
        "schema_version": SCRIPT_VERSION,
        "created_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "source_revision": git_revision(),
        "mode": "direct-crispasr",
        "typewhisper_api_url_for_preparation": args.api_url,
        "asset_root": str(args.asset_root),
        "crispasr_version": CRISPASR_VERSION,
        "backend": args.backend,
        "threads": args.threads,
        "models": args.models,
        "languages": args.languages,
        "samples_per_language": args.samples,
        "sample_count": len(samples),
        "sample_manifest_sha256": manifest_hash,
        "datasets": {
            EUROPE_DATASET[0]: EUROPE_DATASET[1],
            FULL_DATASET[0]: FULL_DATASET[1],
        },
        "normalization": "Unicode NFKC + casefold + punctuation/symbol removal",
        "timing": (
            "Client wall time around each direct localhost CrispASR request; "
            "one persistent server process per model"
        ),
        "post_processing": {
            "mode": "raw CrispASR response",
            "typewhisper_dictionary": False,
            "typewhisper_corrections": False,
            "number_normalization": False,
        },
        "hardware": collect_hardware(),
        "preparation": [],
        "stopped_typewhisper_sidecar_process_ids": [],
        "warmups": [],
        "runs": [],
        "negative_tests": [],
        "model_peak_working_set_bytes": {},
        "model_runtime_status": {},
    }


def load_or_create_result(
    args: argparse.Namespace,
    samples: list[Sample],
    manifest_hash: str,
) -> dict[str, Any]:
    if args.resume and args.output.exists():
        result = json.loads(args.output.read_text(encoding="utf-8"))
        expected = {
            "schema_version": SCRIPT_VERSION,
            "mode": "direct-crispasr",
            "asset_root": str(args.asset_root),
            "backend": args.backend,
            "threads": args.threads,
            "models": args.models,
            "languages": args.languages,
            "samples_per_language": args.samples,
            "sample_manifest_sha256": manifest_hash,
        }
        for key, value in expected.items():
            if result.get(key) != value:
                raise RuntimeError(
                    f"Cannot resume: result field {key!r} does not match this run."
                )
        return result
    return initial_result(args, samples, manifest_hash)


def aggregate_rows(result: dict[str, Any]) -> list[dict[str, Any]]:
    grouped: dict[tuple[str, str], list[dict[str, Any]]] = {}
    for run in result["runs"]:
        if run.get("error"):
            continue
        grouped.setdefault((run["model"], run["language"]), []).append(run)

    rows = []
    for (model, language), runs in grouped.items():
        word_edits = sum(run["word_edits"] for run in runs)
        reference_words = sum(run["reference_words"] for run in runs)
        character_edits = sum(run["character_edits"] for run in runs)
        reference_characters = sum(run["reference_characters"] for run in runs)
        audio_seconds = sum(run["duration_seconds"] for run in runs)
        processing_seconds = sum(run["processing_time_seconds"] for run in runs)
        wall_seconds = sum(run["wall_seconds"] for run in runs)
        rows.append(
            {
                "model": model,
                "language": language,
                "primary_metric": LANGUAGES[language][2],
                "clips": len(runs),
                "wer": word_edits / reference_words if reference_words else math.nan,
                "cer": (
                    character_edits / reference_characters
                    if reference_characters
                    else math.nan
                ),
                "rtfx": (
                    audio_seconds / processing_seconds
                    if processing_seconds
                    else math.inf
                ),
                "wall_rtfx": audio_seconds / wall_seconds if wall_seconds else math.inf,
                "empty_results": sum(
                    not run["normalized_hypothesis"] for run in runs
                ),
                "audio_seconds": audio_seconds,
                "processing_seconds": processing_seconds,
                "wall_seconds": wall_seconds,
                "median_processing_seconds": statistics.median(
                    run["processing_time_seconds"] for run in runs
                ),
            }
        )
    return sorted(
        rows,
        key=lambda row: (
            result["models"].index(row["model"]),
            result["languages"].index(row["language"]),
        ),
    )


def percent(value: float) -> str:
    return "n/a" if math.isnan(value) else f"{value * 100:.2f}%"


def gibibytes(value: int | None) -> str:
    return "n/a" if value is None else f"{value / (1024**3):.2f} GiB"


def markdown_report(result: dict[str, Any]) -> str:
    rows = aggregate_rows(result)
    lines = [
        "# Cohere Transcribe quantization benchmark",
        "",
        f"- Source revision: `{result.get('source_revision')}`",
        f"- Mode: `{result.get('mode')}`",
        f"- Runtime: CrispASR `{result.get('crispasr_version')}`",
        f"- Backend: `{result['backend']}` with {result.get('threads')} threads",
        f"- Samples: {result['samples_per_language']} per language",
        f"- Sample manifest SHA-256: `{result['sample_manifest_sha256']}`",
        "",
        "## Aggregate",
        "",
        "| Model | Model size | Non-CJK macro WER | CJK macro CER | RTFx | Startup | Peak working set |",
        "|---|---:|---:|---:|---:|---:|---:|",
    ]
    for model in result["models"]:
        model_rows = [row for row in rows if row["model"] == model]
        non_cjk = [row["wer"] for row in model_rows if row["language"] not in CJK_LANGUAGES]
        cjk = [row["cer"] for row in model_rows if row["language"] in CJK_LANGUAGES]
        audio_seconds = sum(row["audio_seconds"] for row in model_rows)
        processing_seconds = sum(row["processing_seconds"] for row in model_rows)
        runtime_status = result["model_runtime_status"].get(model, {})
        lines.append(
            "| "
            + " | ".join(
                [
                    f"`{model}`",
                    gibibytes(runtime_status.get("model_file_size_bytes")),
                    percent(statistics.mean(non_cjk)) if non_cjk else "n/a",
                    percent(statistics.mean(cjk)) if cjk else "n/a",
                    f"{audio_seconds / processing_seconds:.2f}x"
                    if processing_seconds
                    else "n/a",
                    f"{runtime_status['startup_seconds']:.2f} s"
                    if runtime_status.get("startup_seconds") is not None
                    else "n/a",
                    gibibytes(
                        result["model_peak_working_set_bytes"].get(model)
                    ),
                ]
            )
            + " |"
        )

    lines.extend(
        [
            "",
            "## Per-language results",
            "",
            "| Model | Language | Primary | WER | CER | RTFx | Empty | Clips |",
            "|---|---|---|---:|---:|---:|---:|---:|",
        ]
    )
    for row in rows:
        primary_value = row[row["primary_metric"]]
        lines.append(
            "| "
            + " | ".join(
                [
                    f"`{row['model']}`",
                    row["language"],
                    f"{row['primary_metric'].upper()} {percent(primary_value)}",
                    percent(row["wer"]),
                    percent(row["cer"]),
                    f"{row['rtfx']:.2f}x",
                    str(row["empty_results"]),
                    str(row["clips"]),
                ]
            )
            + " |"
        )

    lines.extend(
        [
            "",
            "## Silence and noise",
            "",
            "| Model | Input | Output | Processing time |",
            "|---|---|---|---:|",
        ]
    )
    for run in result["negative_tests"]:
        output = run.get("text", "").replace("|", "\\|").replace("\n", " ")
        lines.append(
            f"| `{run['model']}` | {run['sample_id']} | "
            f"`{output}` | {run['processing_time_seconds']:.3f} s |"
        )

    errors = [run for run in result["runs"] if run.get("error")]
    lines.extend(
        [
            "",
            "## Completion",
            "",
            f"- Successful speech clips: {len(result['runs']) - len(errors)}",
            f"- Failed speech clips: {len(errors)}",
            "",
        ]
    )
    return "\n".join(lines)


def checkpoint(args: argparse.Namespace, result: dict[str, Any]) -> None:
    atomic_write_json(args.output, result)
    args.markdown_output.parent.mkdir(parents=True, exist_ok=True)
    args.markdown_output.write_text(markdown_report(result), encoding="utf-8")


def validate_models(args: argparse.Namespace) -> dict[str, dict[str, Any]]:
    response = api_request(args, "GET", "/v1/models")
    models = {
        model["id"]: model
        for model in response.get("models", [])
        if model.get("engine") == "cohere-transcribe"
    }
    missing = [model for model in args.models if model not in models]
    if missing:
        raise RuntimeError(
            "TypeWhisper does not expose the requested Cohere models: "
            + ", ".join(missing)
        )
    return models


def benchmark(args: argparse.Namespace) -> int:
    print("Preparing pinned FLEURS samples...", flush=True)
    samples = prepare_samples(args)
    manifest_hash = sample_manifest_sha256(samples)
    models_before = validate_models(args)
    first_sample = samples[0]
    preparation: list[dict[str, Any]] = []
    if not args.no_prepare_models:
        print("Preparing model downloads...", flush=True)
        for model in args.models:
            started = time.perf_counter()
            response, request_wall = typewhisper_transcribe(
                args,
                model,
                first_sample,
                await_download=True,
            )
            preparation.append(
                {
                    "model": model,
                    "downloaded_before": bool(models_before[model].get("downloaded")),
                    "wall_seconds": time.perf_counter() - started,
                    "request_wall_seconds": request_wall,
                    "processing_time_seconds": response.get("processing_time"),
                }
            )
            print(f"  prepared {model}", flush=True)

    models_after = validate_models(args)
    if args.no_prepare_models:
        missing_downloads = [
            model
            for model in args.models
            if not models_after[model].get("downloaded")
        ]
        if missing_downloads:
            raise RuntimeError(
                "Download these models in TypeWhisper first: "
                + ", ".join(missing_downloads)
            )

    args.asset_root = discover_asset_root(
        args.asset_root,
        args.models,
        args.backend,
    )
    print(f"Using Cohere assets: {args.asset_root}", flush=True)
    stopped_process_ids = stop_typewhisper_sidecar(
        args.asset_root,
        args.stop_typewhisper_sidecar,
    )
    if stopped_process_ids:
        print(
            "Stopped TypeWhisper CrispASR sidecar: "
            + ", ".join(str(process_id) for process_id in stopped_process_ids),
            flush=True,
        )

    result = load_or_create_result(args, samples, manifest_hash)
    result["preparation"] = preparation
    result["stopped_typewhisper_sidecar_process_ids"] = sorted(
        set(result.get("stopped_typewhisper_sidecar_process_ids", []))
        | set(stopped_process_ids)
    )
    result.setdefault("model_runtime_status", {})
    result["runs"] = [run for run in result["runs"] if not run.get("error")]
    checkpoint(args, result)

    completed = {result_key(run) for run in result["runs"]}
    completed_negative = {
        (run["model"], run["sample_id"]) for run in result["negative_tests"]
    }
    negative_samples = make_negative_samples(args.cache_dir / "negative")

    try:
        for model in args.models:
            pending_speech = any(
                (model, sample.language, sample.sample_id) not in completed
                for sample in samples
            )
            pending_negative = any(
                (model, sample.sample_id) not in completed_negative
                for sample in negative_samples
            )
            if (
                not pending_speech
                and not pending_negative
                and model in result["model_peak_working_set_bytes"]
            ):
                print(f"Skipping completed {model}.", flush=True)
                continue

            print(f"Benchmarking {model}...", flush=True)
            server = CrispAsrProcess(args, model)
            try:
                runtime_status = server.start()
                runtime_status["model_file_size_bytes"] = model_path(
                    args.asset_root,
                    model,
                ).stat().st_size
                result["model_runtime_status"][model] = runtime_status

                first_response, first_wall = server.transcribe(first_sample)
                second_response, second_wall = server.transcribe(first_sample)
                result["warmups"] = [
                    warmup
                    for warmup in result["warmups"]
                    if warmup["model"] != model
                ]
                result["warmups"].append(
                    {
                        "model": model,
                        "server_startup_seconds": runtime_status[
                            "startup_seconds"
                        ],
                        "load_and_first_inference_wall_seconds": (
                            runtime_status["startup_seconds"] + first_wall
                        ),
                        "first_inference_seconds": first_wall,
                        "first_output": first_response.get("text", ""),
                        "second_warmup_wall_seconds": second_wall,
                        "second_output": second_response.get("text", ""),
                    }
                )
                checkpoint(args, result)

                model_samples = list(samples)
                for index, sample in enumerate(model_samples, start=1):
                    key = (model, sample.language, sample.sample_id)
                    if key in completed:
                        continue
                    try:
                        response, wall_seconds = server.transcribe(sample)
                        metrics = score(
                            sample.reference,
                            response.get("text", ""),
                        )
                        result["runs"].append(
                            {
                                "model": model,
                                "language": sample.language,
                                "language_hint": sample.language_hint,
                                "sample_id": sample.sample_id,
                                "reference": sample.reference,
                                "hypothesis": response.get("text", ""),
                                "detected_language": response.get("language"),
                                "duration_seconds": float(
                                    response.get("duration")
                                    or sample.duration_seconds
                                ),
                                "processing_time_seconds": wall_seconds,
                                "wall_seconds": wall_seconds,
                                **metrics,
                            }
                        )
                        completed.add(key)
                    except Exception as error:
                        result["runs"].append(
                            {
                                "model": model,
                                "language": sample.language,
                                "language_hint": sample.language_hint,
                                "sample_id": sample.sample_id,
                                "reference": sample.reference,
                                "duration_seconds": sample.duration_seconds,
                                "error": str(error),
                            }
                        )
                        if (
                            server.process is None
                            or server.process.poll() is not None
                        ):
                            raise
                    if index % args.checkpoint_every == 0:
                        checkpoint(args, result)
                        print(
                            f"  {index}/{len(model_samples)} speech clips",
                            flush=True,
                        )

                for sample in negative_samples:
                    key = (model, sample.sample_id)
                    if key in completed_negative:
                        continue
                    response, wall_seconds = server.transcribe(sample)
                    result["negative_tests"].append(
                        {
                            "model": model,
                            "sample_id": sample.sample_id,
                            "text": response.get("text", ""),
                            "processing_time_seconds": wall_seconds,
                            "wall_seconds": wall_seconds,
                        }
                    )
                    completed_negative.add(key)
            finally:
                peak_working_set = server.peak_working_set()
                if peak_working_set is not None:
                    result["model_peak_working_set_bytes"][
                        model
                    ] = peak_working_set
                server.stop()
                checkpoint(args, result)
    except KeyboardInterrupt:
        checkpoint(args, result)
        print("\nInterrupted; checkpoint saved.", file=sys.stderr)
        return 130

    checkpoint(args, result)
    print(markdown_report(result))
    errors = [run for run in result["runs"] if run.get("error")]
    return 2 if errors else 0


def main() -> int:
    args = parse_args()
    try:
        return benchmark(args)
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
