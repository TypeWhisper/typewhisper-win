#!/usr/bin/env python3
"""Granite Speech inference server for TypeWhisper plugin.

Modes:
  --check    Check if packages + model are ready (prints JSON status)
  --setup    Download HF model files (prints JSON progress lines)
  --serve    Start JSON-line inference server on stdin/stdout
"""

import argparse
import json
import math
import os
import sys
import time

MODEL_NAME = "ibm-granite/granite-4.0-1b-speech"
MODEL_REVISION = "bd87ab862416353633ea431fe49b1614003623c5"

# Without long path support Windows rejects paths of 260 characters and more (MAX_PATH).
MAX_PATH = 260


def model_dir():
    """Short folder that holds the model files directly, without the Hugging Face cache levels."""
    return os.environ.get("TYPEWHISPER_MODEL_DIR") or None


def legacy_cache_dir():
    """Hugging Face hub cache that versions up to 1.2.8 downloaded the model into."""
    home = os.environ.get("TYPEWHISPER_LEGACY_HF_HOME")
    return os.path.join(home, "hub") if home else None


def model_source():
    """Returns the model location and loading options: the model folder, or the cache of an older install."""
    directory = model_dir()
    if directory and os.path.isfile(os.path.join(directory, "config.json")):
        return directory, {}
    return MODEL_NAME, {"revision": MODEL_REVISION, "cache_dir": legacy_cache_dir()}


def long_paths_enabled():
    if os.name != "nt":
        return True
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\FileSystem") as key:
            return winreg.QueryValueEx(key, "LongPathsEnabled")[0] == 1
    except OSError:
        return False


def windows_path_length(path):
    """Length in UTF-16 code units, which Windows counts: characters outside the BMP take two."""
    return len(path.encode("utf-16-le")) // 2


def path_length_error(directory, filenames):
    """Explains a model folder whose files Windows cannot open, or returns None.

    huggingface_hub switches to extended-length paths for its own lock, metadata and partial files, but
    transformers later opens the model files through ordinary paths.
    """
    if not filenames or long_paths_enabled():
        return None
    longest = max(windows_path_length(os.path.join(os.path.abspath(directory), name)) for name in filenames)
    if longest < MAX_PATH:
        return None
    return (
        f"The model folder path is too long: model files would need {longest} characters, "
        f"Windows allows {MAX_PATH - 1} without long path support. "
        f"Use a profile folder with a shorter path, or enable long path support in Windows, then retry. "
        f"Model folder: {directory}"
    )


def adopt_legacy_file(filename, directory, legacy_cache):
    """Moves a model file an older install cached into the model folder, so setup does not download it again.

    The download afterwards still checks the file against the pinned revision.
    """
    if not legacy_cache:
        return False
    target = os.path.join(directory, filename)
    if os.path.exists(target):
        return False
    try:
        from huggingface_hub import try_to_load_from_cache

        cached = try_to_load_from_cache(MODEL_NAME, filename, cache_dir=legacy_cache, revision=MODEL_REVISION)
        if not isinstance(cached, str):
            return False
        # Snapshot entries are symlinks to blobs where Windows allows symlinks; move the blob itself.
        source = os.path.realpath(cached)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        os.replace(source, target)
        return True
    except OSError:
        return False


def inference_dtype(torch, device):
    return torch.bfloat16 if device == "cuda" and torch.cuda.is_bf16_supported() else torch.float32


def generation_budget(duration, available_context):
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError("Audio must have a positive, finite duration.")
    budget = min(32768, max(512, math.ceil(duration * 16) + 128), available_context)
    if budget <= 0:
        raise ValueError("Recording exceeds the model context. Split it into shorter recordings.")
    return budget


def check_generation_complete(token_ids, budget, eos_token_id):
    eos_ids = eos_token_id if isinstance(eos_token_id, (list, tuple)) else [eos_token_id]
    if len(token_ids) >= budget and (not token_ids or token_ids[-1] not in eos_ids):
        raise RuntimeError("Transcription reached the output limit. Split the recording into shorter parts; partial text was not accepted.")


def transcription_question(translate, language):
    question = "Translate the speech into English." if translate else "Transcribe the speech exactly as spoken, preserving the original language."
    languages = {"de": "German", "en": "English", "fr": "French", "es": "Spanish", "pt": "Portuguese", "ja": "Japanese"}
    if language in languages:
        question += f" The spoken language is {languages[language]}."
    return question


def respond(data):
    sys.stdout.write(json.dumps(data) + "\n")
    sys.stdout.flush()


def cmd_check():
    """Check if packages are installed and model is cached."""
    issues = []
    try:
        import torch  # noqa: F401
    except ImportError:
        issues.append("torch not installed")
    try:
        import transformers  # noqa: F401
    except ImportError:
        issues.append("transformers not installed")
    try:
        import soundfile  # noqa: F401
    except ImportError:
        issues.append("soundfile not installed")

    if not issues:
        source, options = model_source()
        if source == MODEL_NAME:
            try:
                from huggingface_hub import try_to_load_from_cache

                result = try_to_load_from_cache(MODEL_NAME, "config.json", **options)
                if not isinstance(result, str):
                    issues.append("model not downloaded")
            except Exception:
                issues.append("model not downloaded")

    respond({"ready": len(issues) == 0, "issues": issues})


def cmd_setup():
    """Download HF model files, reporting progress as JSON lines.

    Packages are already installed by the C# host via pip. The files go straight into the model folder
    instead of the Hugging Face cache, whose repository, blob hash and partial-file levels made the
    paths too long for some Windows profiles.
    """
    respond({"progress": 0.0, "phase": "model"})

    directory = model_dir()
    if not directory:
        respond({"error": "No model folder was provided."})
        sys.exit(1)

    from huggingface_hub import HfApi, hf_hub_download

    api = HfApi()

    try:
        info = api.model_info(MODEL_NAME, revision=MODEL_REVISION, files_metadata=True)
        sizes = {item.rfilename: item.size or 0 for item in info.siblings}
        all_files = list(sizes)
    except Exception as e:
        respond({"error": f"Failed to list model files: {e}"})
        sys.exit(1)

    model_files = [
        f
        for f in all_files
        if f.endswith((".safetensors", ".json", ".txt", ".model", ".py", ".jinja"))
    ]

    path_error = path_length_error(directory, model_files)
    if path_error:
        respond({"error": path_error})
        sys.exit(1)

    total_bytes = sum(sizes[f] for f in model_files)
    completed_bytes = 0
    legacy_cache = legacy_cache_dir()
    from tqdm.auto import tqdm

    class DownloadProgress(tqdm):
        def __init__(self, *args, **kwargs):
            self.downloaded = kwargs.get("initial", 0)
            self.last_report = 0.0
            kwargs["disable"] = True
            super().__init__(*args, **kwargs)

        def update(self, amount=1):
            self.downloaded += amount
            now = time.monotonic()
            if now - self.last_report >= 0.25:
                self.last_report = now
                respond({"progress": min(1.0, (completed_bytes + self.downloaded) / max(1, total_bytes)), "phase": "model"})
            return super().update(amount)

    max_retries = 3

    for filename in model_files:
        adopt_legacy_file(filename, directory, legacy_cache)
        for attempt in range(max_retries):
            try:
                hf_hub_download(MODEL_NAME, filename, revision=MODEL_REVISION, local_dir=directory, tqdm_class=DownloadProgress)
                break
            except Exception as e:
                if attempt < max_retries - 1:
                    delay = 2**attempt
                    respond(
                        {
                            "warning": f"Retry {attempt + 1}/{max_retries} for {filename}: {e}",
                            "progress": completed_bytes / max(1, total_bytes),
                            "phase": "model",
                        }
                    )
                    time.sleep(delay)
                else:
                    respond({"error": f"Failed to download {filename} after {max_retries} attempts: {e}"})
                    sys.exit(1)

        completed_bytes += sizes[filename]
        respond({"progress": completed_bytes / max(1, total_bytes), "phase": "model"})

    respond({"progress": 1.0, "phase": "done"})


def cmd_serve():
    """Run inference server reading JSON commands from stdin."""
    import warnings

    warnings.filterwarnings("ignore")
    os.environ["TOKENIZERS_PARALLELISM"] = "false"

    import base64
    import io

    import torch
    import soundfile as sf
    from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor

    device = "cuda" if os.environ.get("TYPEWHISPER_DEVICE", "Auto") != "Cpu" and torch.cuda.is_available() else "cpu"
    torch.set_num_threads(min(8, os.cpu_count() or 4))
    model = None
    processor = None
    tokenizer = None

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            cmd = json.loads(line)
        except json.JSONDecodeError:
            respond({"error": "Invalid JSON"})
            continue

        action = cmd.get("cmd")
        req_id = cmd.get("req_id")

        if action == "ping":
            respond({"status": "ok", "req_id": req_id})

        elif action == "load":
            try:
                if os.environ.get("TYPEWHISPER_DEVICE") == "NvidiaCuda" and device != "cuda":
                    raise RuntimeError("NVIDIA CUDA is unavailable. Select CPU or install a compatible NVIDIA driver.")
                source, options = model_source()
                processor = AutoProcessor.from_pretrained(source, local_files_only=True, **options)
                tokenizer = processor.tokenizer
                model = AutoModelForSpeechSeq2Seq.from_pretrained(
                    source, local_files_only=True,
                    torch_dtype=inference_dtype(torch, device), **options
                )
                model = model.to(device).eval()
                respond({"status": "ok", "device": device, "req_id": req_id})
            except Exception as e:
                respond({"error": str(e), "req_id": req_id})

        elif action == "transcribe":
            if model is None or processor is None or tokenizer is None:
                respond({"error": "Model not loaded", "req_id": req_id})
                continue

            try:
                audio_bytes = base64.b64decode(cmd["audio_base64"])
                translate = cmd.get("translate", False)

                audio_data, sr = sf.read(io.BytesIO(audio_bytes))
                if len(audio_data.shape) > 1:
                    audio_data = audio_data.mean(axis=1)
                duration = len(audio_data) / sr

                if sr != 16000:
                    import torchaudio.functional as F

                    wav = torch.tensor(audio_data, dtype=torch.float32).unsqueeze(0)
                    wav = F.resample(wav, sr, 16000)
                else:
                    wav = torch.tensor(audio_data, dtype=torch.float32).unsqueeze(0)

                question = transcription_question(translate, cmd.get("language"))

                chat = [{"role": "user", "content": f"<|audio|>{question}"}]
                prompt = tokenizer.apply_chat_template(
                    chat, tokenize=False, add_generation_prompt=True
                )

                model_inputs = processor(
                    prompt, wav, device=device, return_tensors="pt"
                )
                model_inputs = model_inputs.to(device)
                num_input_tokens = model_inputs["input_ids"].shape[-1]
                text_config = model.config.get_text_config()
                budget = generation_budget(duration, text_config.max_position_embeddings - num_input_tokens - 16)
                with torch.inference_mode():
                    outputs = model.generate(
                        **model_inputs,
                        max_new_tokens=budget,
                        do_sample=False,
                        num_beams=1,
                    )

                new_tokens = outputs[0, num_input_tokens:].unsqueeze(0)
                check_generation_complete(new_tokens[0].tolist(), budget, model.generation_config.eos_token_id)
                text = tokenizer.batch_decode(
                    new_tokens,
                    add_special_tokens=False,
                    skip_special_tokens=True,
                )[0]

                respond({"text": text.strip(), "duration": duration, "req_id": req_id})
            except Exception as e:
                respond({"error": str(e), "req_id": req_id})

        elif action == "unload":
            model = None
            processor = None
            tokenizer = None
            import gc

            gc.collect()
            respond({"status": "ok", "req_id": req_id})

        elif action == "quit":
            respond({"status": "ok", "req_id": req_id})
            break

        else:
            respond({"error": f"Unknown command: {action}", "req_id": req_id})


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--check", action="store_true")
    group.add_argument("--setup", action="store_true")
    group.add_argument("--serve", action="store_true")
    args = parser.parse_args()

    if args.check:
        cmd_check()
    elif args.setup:
        cmd_setup()
    elif args.serve:
        cmd_serve()
