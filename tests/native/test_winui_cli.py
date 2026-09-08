"""Native CLI acceptance against an already running, isolated Windows test profile.

No application launch, PATH edits, model downloads/deletions, or normal profile writes.
Only test names, outcomes and timing are saved; no tokens or response bodies.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--audio", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, default=Path("artifacts/cli-native-evidence.json"))
    parser.add_argument("--skip-dictation", action="store_true", help="Run partial acceptance without microphone capture; evidence is not marked fully passed")
    args = parser.parse_args()
    profile = args.profile.resolve(strict=True)
    test_root = (Path(tempfile.gettempdir()) / "TypeWhisper-WinUI-TestProfiles").resolve()
    if profile == test_root or not profile.is_relative_to(test_root):
        raise RuntimeError("An isolated profile beneath TEMP/TypeWhisper-WinUI-TestProfiles is required.")
    output = json.loads((profile / "dictation-output.json").read_text(encoding="utf-8-sig"))
    if output.get("AutoPaste") is not False or output.get("SaveToHistory") is not False:
        raise RuntimeError("Disable AutoPaste and SaveToHistory in the isolated profile before running.")
    cli = args.cli.resolve(strict=True)
    audio = args.audio.resolve(strict=True)
    environment = os.environ.copy()
    environment.pop("TYPEWHISPER_API_TOKEN", None)
    environment.pop("TYPEWHISPER_PROFILE", None)
    evidence = {"started_utc": datetime.now(timezone.utc).isoformat(), "passed": False, "checks": []}

    def digest(path):
        return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None

    def check(name, condition):
        evidence["checks"].append({"name": name, "passed": bool(condition)})
        if not condition:
            raise AssertionError("Acceptance check failed: " + name)

    def run(*command, stdin=None, json_output=True, allowed=(0,)):
        arguments = [str(cli), *command, "--profile", str(profile)]
        if json_output:
            arguments.append("--json")
        result = subprocess.run(arguments, input=stdin, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, env=environment, timeout=180)
        # Never include stdout, stderr, argument values or credentials in errors/evidence.
        if result.returncode not in allowed:
            evidence["failure"] = {"command": command[0], "exit_code": result.returncode}
            raise AssertionError("CLI returned an unexpected exit code for " + command[0])
        if result.returncode != 0 and (not result.stderr.startswith(b"Error:") or b" at " in result.stderr):
            raise AssertionError("CLI failure did not use a clean stderr error.")
        if result.returncode == 0 and result.stderr:
            raise AssertionError("Successful CLI command wrote to stderr.")
        if json_output and result.stdout.strip():
            return result.returncode, json.loads(result.stdout)
        return result.returncode, result.stdout.decode("utf-8", errors="replace")

    history_before = digest(profile / "history.json")
    audio_before = digest(audio)
    started = time.monotonic()
    dictation_id = None
    model_unloaded = False
    engine = model_id = None
    try:
        _, status = run("status")
        check("profile_discovery_status_ready", status.get("status") == "ready")
        _, models = run("models", "list")
        active = [model for model in models["models"] if model.get("active")]
        check("local_canary_ready", len(active) == 1 and not active[0]["cloud"]
              and "canary" in active[0]["id"] and active[0]["downloaded"])
        engine, model_id = active[0]["engine"], active[0]["id"]
        _, unloaded = run("models", "unload", "--engine", engine)
        model_unloaded = True
        check("model_unload", unloaded["status"] == "unloaded")
        _, loaded = run("models", "load", "--engine", engine, "--model", model_id)
        model_unloaded = False
        check("model_load", loaded["status"] == "ready")
        _, history = run("history", "search", "HttpApiParity20260908", "--limit", "2", "--offset", "0")
        check("history_search_pagination", isinstance(history["entries"], list)
              and history["limit"] == 2 and history["offset"] == 0)
        _, last = run("last")
        check("history_last", isinstance(last["entries"], list) and len(last["entries"]) <= 1
              and last["limit"] == 1)
        _, local = run("transcribe", str(audio), "--language", "en")
        check("local_file_transcription", bool(local.get("text", "").strip()))
        _, streamed = run("transcribe", "-", "--language", "en", stdin=audio.read_bytes())
        check("stdin_transcription", bool(streamed.get("text", "").strip())
              and streamed["text"].strip() == local["text"].strip())
        # Keep potentially sensitive backup bytes inside this isolated profile and remove them.
        backup = profile / ("cli-acceptance-backup-" + str(os.getpid()) + ".json")
        try:
            _, exported = run("export", str(backup))
            check("settings_export", exported.get("file") == str(backup) and exported.get("bytes", 0) > 0 and backup.exists()
                  and isinstance(json.loads(backup.read_text(encoding="utf-8-sig")), dict))
        finally:
            backup.unlink(missing_ok=True)
        evidence["unchanged_import"] = "not_run; asynchronous restore covered separately"
        _, validation = run("status", "unexpected", json_output=False, allowed=(1,))
        check("invalid_arguments_clean_failure", validation == "")
        # Hold a bound, non-listening local socket so the chosen port cannot host another app.
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reserved:
            reserved.bind(("127.0.0.1", 0))
            _, unavailable = run("status", "--port", str(reserved.getsockname()[1]),
                                 json_output=False, allowed=(2,))
            check("no_server_clean_failure", unavailable == "")
        if args.skip_dictation:
            evidence["dictation"] = "explicitly_skipped; partial_acceptance_only"
        else:
            _, recording = run("dictation", "start")
            dictation_id = recording["id"]
            check("dictation_start", recording["status"] == "recording")
            _, recording_status = run("dictation", "status")
            check("dictation_status", recording_status["state"] == "recording")
            time.sleep(0.4)
            _, stopped = run("dictation", "stop")
            check("dictation_stop", stopped["status"] == "stopped" and stopped["id"] == dictation_id)
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                exit_code, result = run("dictation", "result", dictation_id, allowed=(0, 3))
                state = result.get("status")
                if state in ("completed", "failed"):
                    check("dictation_terminal_result", (state == "completed" and exit_code == 0
                          and isinstance(result.get("transcription"), dict))
                          or (state == "failed" and exit_code == 3 and bool(result.get("error"))))
                    evidence["dictation_terminal_state"] = state
                    dictation_id = None
                    break
                check("dictation_pending_result", state in ("recording", "processing") and exit_code == 0)
                time.sleep(0.5)
            else:
                raise AssertionError("Dictation did not reach a terminal state in time.")
        check("history_unchanged", history_before == digest(profile / "history.json"))
        check("source_unchanged", audio_before == digest(audio))
        evidence["passed"] = not args.skip_dictation
        evidence["partial_passed"] = args.skip_dictation
    finally:
        if dictation_id:
            # Stop only if this test still owns an active recording.
            try:
                _, state = run("dictation", "status")
                if state.get("state") == "recording":
                    run("dictation", "stop")
            except Exception:
                evidence["cleanup_dictation_failed"] = True
        if model_unloaded:
            try:
                run("models", "load", "--engine", engine, "--model", model_id)
            except Exception:
                evidence["cleanup_model_reload_failed"] = True
        evidence["elapsed_seconds"] = round(time.monotonic() - started, 2)
        args.evidence.parent.mkdir(parents=True, exist_ok=True)
        args.evidence.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"passed": evidence["passed"], "checks": len(evidence["checks"])}))


if __name__ == "__main__":
    main()
