#!/usr/bin/env python3
"""Benchmark the local Cohere Transcribe GGUF variants through TypeWhisper.

The runner downloads a pinned subset of the public FLEURS test corpus, prepares
the selected models through TypeWhisper's local API, and records accuracy,
throughput, wall time, peak CrispASR working set, and silence/noise behavior.
It intentionally uses only the Python standard library.
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


SCRIPT_VERSION = 1
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
        description="Benchmark Cohere GGUF quantizations through the TypeWhisper API."
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
        default=Path("artifacts/cohere-quantization-benchmark/results.json"),
    )
    parser.add_argument(
        "--markdown-output",
        type=Path,
        default=Path("artifacts/cohere-quantization-benchmark/results.md"),
    )
    parser.add_argument(
        "--backend",
        default="configured in TypeWhisper",
        help=(
            "Expected active backend reported by TypeWhisper, for example "
            "nvidia-cuda, amd-vulkan, or cpu."
        ),
    )
    parser.add_argument(
        "--allow-dictionary",
        action="store_true",
        help="Allow enabled dictionary terms or corrections to modify benchmark output.",
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


def transcribe(
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
    return hardware


def crispasr_peak_working_set() -> int | None:
    if os.name != "nt":
        return None
    value = powershell_json(
        """
$process = Get-Process crispasr -ErrorAction SilentlyContinue |
  Sort-Object StartTime -Descending |
  Select-Object -First 1
if ($process) {
  [long]$process.PeakWorkingSet64 | ConvertTo-Json -Compress
}
"""
    )
    return int(value) if value is not None else None


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
        "api_url": args.api_url,
        "backend": args.backend,
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
        "hardware": collect_hardware(),
        "preparation": [],
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
        f"- Backend: `{result['backend']}`",
        f"- Samples: {result['samples_per_language']} per language",
        f"- Sample manifest SHA-256: `{result['sample_manifest_sha256']}`",
        "",
        "## Aggregate",
        "",
        "| Model | Non-CJK macro WER | CJK macro CER | Weighted RTFx | Wall RTFx | Peak working set |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for model in result["models"]:
        model_rows = [row for row in rows if row["model"] == model]
        non_cjk = [row["wer"] for row in model_rows if row["language"] not in CJK_LANGUAGES]
        cjk = [row["cer"] for row in model_rows if row["language"] in CJK_LANGUAGES]
        audio_seconds = sum(row["audio_seconds"] for row in model_rows)
        processing_seconds = sum(row["processing_seconds"] for row in model_rows)
        wall_seconds = sum(row["wall_seconds"] for row in model_rows)
        lines.append(
            "| "
            + " | ".join(
                [
                    f"`{model}`",
                    percent(statistics.mean(non_cjk)) if non_cjk else "n/a",
                    percent(statistics.mean(cjk)) if cjk else "n/a",
                    f"{audio_seconds / processing_seconds:.2f}x"
                    if processing_seconds
                    else "n/a",
                    f"{audio_seconds / wall_seconds:.2f}x" if wall_seconds else "n/a",
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
            "| Model | Language | Primary | WER | CER | RTFx | Wall RTFx | Empty | Clips |",
            "|---|---|---|---:|---:|---:|---:|---:|---:|",
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
                    f"{row['wall_rtfx']:.2f}x",
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


def validate_post_processing(args: argparse.Namespace) -> dict[str, Any]:
    terms = api_request(args, "GET", "/v1/dictionary/terms")
    corrections = api_request(args, "GET", "/v1/dictionary/corrections")
    term_count = int(terms.get("count", len(terms.get("terms", []))))
    correction_count = int(
        corrections.get("count", len(corrections.get("corrections", [])))
    )
    if not args.allow_dictionary and (term_count or correction_count):
        raise RuntimeError(
            "The benchmark requires an empty TypeWhisper dictionary. "
            f"Found {term_count} terms and {correction_count} corrections. "
            "Use a clean profile or pass --allow-dictionary to record a non-standard run."
        )
    return {
        "dictionary_terms": term_count,
        "dictionary_corrections": correction_count,
        "number_normalization": False,
        "dictionary_override_allowed": bool(args.allow_dictionary),
    }


def validate_active_backend(
    args: argparse.Namespace,
    model: str,
) -> dict[str, Any]:
    status = api_request(args, "GET", "/v1/status")
    acceleration = status.get("acceleration") or {}
    active_backend = acceleration.get("active_backend")
    if (
        args.backend != "configured in TypeWhisper"
        and active_backend != args.backend
    ):
        raise RuntimeError(
            f"{model} loaded with backend {active_backend!r}, "
            f"but {args.backend!r} was requested."
        )
    return status


def benchmark(args: argparse.Namespace) -> int:
    print("Preparing pinned FLEURS samples...", flush=True)
    samples = prepare_samples(args)
    manifest_hash = sample_manifest_sha256(samples)
    result = load_or_create_result(args, samples, manifest_hash)
    result.setdefault("model_runtime_status", {})
    models_before = validate_models(args)
    result["post_processing"] = validate_post_processing(args)
    first_sample = samples[0]

    prepared_models = {entry["model"] for entry in result["preparation"]}
    if not args.no_prepare_models:
        print("Preparing model downloads...", flush=True)
        for model in args.models:
            if model in prepared_models:
                continue
            started = time.perf_counter()
            response, request_wall = transcribe(
                args,
                model,
                first_sample,
                await_download=True,
            )
            result["preparation"].append(
                {
                    "model": model,
                    "downloaded_before": bool(models_before[model].get("downloaded")),
                    "wall_seconds": time.perf_counter() - started,
                    "request_wall_seconds": request_wall,
                    "processing_time_seconds": response.get("processing_time"),
                }
            )
            checkpoint(args, result)
            print(f"  prepared {model}", flush=True)
    elif args.no_prepare_models:
        missing_downloads = [
            model
            for model in args.models
            if not models_before[model].get("downloaded")
        ]
        if missing_downloads:
            raise RuntimeError(
                "Download these models in TypeWhisper first: "
                + ", ".join(missing_downloads)
            )

    completed = {result_key(run) for run in result["runs"]}
    completed_negative = {
        (run["model"], run["sample_id"]) for run in result["negative_tests"]
    }
    negative_samples = make_negative_samples(args.cache_dir / "negative")

    try:
        for model in args.models:
            print(f"Benchmarking {model}...", flush=True)
            if not any(warmup["model"] == model for warmup in result["warmups"]):
                first_response, first_wall = transcribe(
                    args,
                    model,
                    first_sample,
                    await_download=False,
                )
                second_response, second_wall = transcribe(
                    args,
                    model,
                    first_sample,
                    await_download=False,
                )
                result["warmups"].append(
                    {
                        "model": model,
                        "load_and_first_inference_wall_seconds": first_wall,
                        "first_processing_time_seconds": first_response.get(
                            "processing_time"
                        ),
                        "second_warmup_wall_seconds": second_wall,
                        "second_processing_time_seconds": second_response.get(
                            "processing_time"
                        ),
                    }
                )
                checkpoint(args, result)
            else:
                transcribe(
                    args,
                    model,
                    first_sample,
                    await_download=False,
                )
            result["model_runtime_status"][model] = validate_active_backend(
                args,
                model,
            )
            checkpoint(args, result)

            model_samples = [sample for sample in samples]
            for index, sample in enumerate(model_samples, start=1):
                key = (model, sample.language, sample.sample_id)
                if key in completed:
                    continue
                try:
                    response, wall_seconds = transcribe(
                        args,
                        model,
                        sample,
                        await_download=False,
                    )
                    metrics = score(sample.reference, response.get("text", ""))
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
                                response.get("duration") or sample.duration_seconds
                            ),
                            "processing_time_seconds": float(
                                response.get("processing_time") or wall_seconds
                            ),
                            "wall_seconds": wall_seconds,
                            **metrics,
                        }
                    )
                except Exception as error:  # Continue to preserve long benchmark runs.
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
                completed.add(key)
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
                response, wall_seconds = transcribe(
                    args,
                    model,
                    sample,
                    await_download=False,
                )
                result["negative_tests"].append(
                    {
                        "model": model,
                        "sample_id": sample.sample_id,
                        "text": response.get("text", ""),
                        "processing_time_seconds": float(
                            response.get("processing_time") or wall_seconds
                        ),
                        "wall_seconds": wall_seconds,
                    }
                )
                completed_negative.add(key)

            result["model_peak_working_set_bytes"][model] = (
                crispasr_peak_working_set()
            )
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
