#!/usr/bin/env python3
"""Granite Speech inference server for TypeWhisper plugin.

Modes:
  --check    Check if packages + model are ready (prints JSON status)
  --setup    Download HF model files (prints JSON progress lines)
  --serve    Start JSON-line inference server on stdin/stdout
"""

import argparse
import json
import os
import sys
import time

MODEL_NAME = "ibm-granite/granite-4.0-1b-speech"


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
        try:
            from huggingface_hub import try_to_load_from_cache

            result = try_to_load_from_cache(MODEL_NAME, "config.json")
            if result is None:
                issues.append("model not downloaded")
        except Exception:
            issues.append("model not downloaded")

    respond({"ready": len(issues) == 0, "issues": issues})


def cmd_setup():
    """Download HF model files, reporting progress as JSON lines.

    Packages are already installed by the C# host via pip.
    """
    respond({"progress": 0.0, "phase": "model"})

    from huggingface_hub import HfApi, hf_hub_download

    api = HfApi()

    try:
        all_files = api.list_repo_files(MODEL_NAME)
    except Exception as e:
        respond({"error": f"Failed to list model files: {e}"})
        sys.exit(1)

    model_files = [
        f
        for f in all_files
        if f.endswith((".safetensors", ".json", ".txt", ".model", ".py"))
    ]

    total = len(model_files)
    max_retries = 3

    for i, filename in enumerate(model_files):
        for attempt in range(max_retries):
            try:
                hf_hub_download(MODEL_NAME, filename)
                break
            except Exception as e:
                if attempt < max_retries - 1:
                    delay = 2**attempt
                    respond(
                        {
                            "warning": f"Retry {attempt + 1}/{max_retries} for {filename}: {e}",
                            "progress": i / total,
                            "phase": "model",
                        }
                    )
                    time.sleep(delay)
                else:
                    respond({"error": f"Failed to download {filename} after {max_retries} attempts: {e}"})
                    sys.exit(1)

        respond({"progress": (i + 1) / total, "phase": "model"})

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
                processor = AutoProcessor.from_pretrained(MODEL_NAME)
                tokenizer = processor.tokenizer
                model = AutoModelForSpeechSeq2Seq.from_pretrained(
                    MODEL_NAME, torch_dtype=torch.float32
                )
                respond({"status": "ok", "req_id": req_id})
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

                if translate:
                    question = "Translate the speech into English."
                else:
                    question = "Transcribe the speech exactly as spoken, preserving the original language."

                chat = [{"role": "user", "content": f"<|audio|>{question}"}]
                prompt = tokenizer.apply_chat_template(
                    chat, tokenize=False, add_generation_prompt=True
                )

                model_inputs = processor(
                    prompt, wav, device="cpu", return_tensors="pt"
                )
                outputs = model.generate(
                    **model_inputs,
                    max_new_tokens=500,
                    do_sample=False,
                    num_beams=1,
                )

                num_input_tokens = model_inputs["input_ids"].shape[-1]
                new_tokens = outputs[0, num_input_tokens:].unsqueeze(0)
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
