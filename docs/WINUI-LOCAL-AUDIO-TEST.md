# Local Windows audio and screenshot acceptance

Use this for a real speaker → microphone → provider → pasted-document test of a portable plugin. Direct WAV/API tests do not cover this path. The helper is provider-independent and uses the credentials already saved in the running development profile. Cloud providers receive microphone audio and may charge for the short recording.

## Session and development host

1. Use the project's development launcher with this checkout as the source; on the maintained Windows development machine:
   ```powershell
   & F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run --winui (Get-Location).Path
   ```
   Do not launch production or a transient worktree executable. Coordinate shared-output builds: another checkout can replace the published host. Compare the published host DLL hash with this checkout's build before claiming host UI acceptance.
2. Keep the local desktop logged in and unlocked. RDP audio redirection replaces the physical endpoints in that session. Merely disconnecting RDP leaves the session disconnected.
3. To transfer an RDP session to the physical console, first run `query session`, then run `tscon <your-session-id> /dest:console` from an elevated PowerShell. Use the current session ID, not a hardcoded value. This disconnects RDP; arrange the handover with the person watching first. If access is denied, ask the local operator to perform it or sign in locally. Do not automate authentication or disable locking policies.
4. Verify `query session` now reports your session as `console` and `Active`. Screenshots can provide visual feedback without reconnecting RDP.

## Prepare and run

Requires Windows, .NET 10 SDK, an installed English Windows speech voice, an enabled/configured provider, and the local HTTP API in the dev profile. The script never prints or saves its discovery token and does not alter API keys.

```powershell
# Lists active capture/render endpoints and the Windows default.
./eng/Test-WinUILocalAudio.ps1 -Mode Devices

# Optional: generate only the synthetic WAV; no microphone or provider request.
./eng/Test-WinUILocalAudio.ps1 -Mode Prepare

# Example; replace the exact device name and provider/model for your machine.
./eng/Test-WinUILocalAudio.ps1 -Mode Run `
  -Engine assemblyai -Model universal-3-5-pro `
  -OutputDeviceName 'Speakers (Creative Pebble Pro)'
```

Before Run, use the dev UI to select the intended microphone and record the current settings. Temporarily disable **Lower audio while recording** and **Pause media during recording**. Enable **Auto paste**, and choose a plain transcription task without workflow/provider overrides. Open a new blank Notepad document. The script gives you five seconds to focus it; an automation agent should focus the observed document first and run the script in the background with `-FocusDelaySeconds 0`.

Run selects the requested model, starts the app's normal microphone dictation through its API, plays up to 20 seconds of locally synthesized speech through the chosen physical output, stops recording in `finally`, and polls with a deadline. It checks the raw sentence, provider, model, and reported Notepad target. Existing dictionary corrections can legitimately change the final text. Model selection is restored even after a failure, with a bounded retry while processing drains. Restore the audio preferences through the UI afterward. Each invocation writes to a fresh ignored `artifacts/local-audio/<run-id>` directory; `-OutputDirectory` accepts a new custom location.

The script does not claim successful paste merely because the transcription API succeeded, and it does not test physical hotkeys. Verify the visible Notepad text against `result.json`'s final `text`, then capture the result. Unexpected speech, punctuation, a muted mic, headphones, or echo cancellation can fail the exact-match assertion: inspect the evidence rather than loosening it to claim success. If the script is forcibly killed, check recording status and restore settings manually.

Run `./eng/Test-WinUILocalAudio.Tests.ps1` for headless lifecycle regression checks. Fake audio and HTTP boundaries exercise success, failed model selection, a lost response after the server accepts recording start, and a failed stop followed by cleanup retry. These checks also run in Windows/Linux CI and do not capture or upload audio.

## Screenshots with Computer Use

Use the installed Computer Use skill and its `node_repl` / `@oai/sky` interface for native UI operations. Observe the returned window before clicking. Do not target a guessed window ID or type the expected transcript into the test document. Capture the blank target before the run, relevant settings when changed, and the actual pasted result afterward. Show these screenshots to the operator in the conversation.

After initializing the skill, select the unique returned Notepad window:

```javascript
var windows = await sky.list_windows();
nodeRepl.write(windows);
// Inspect returned windows, then use the actual Notepad entry in the next call.
```

Capture with `sky.get_window_state({ window: noteWindow, include_screenshot: true })`. The tool displays the screenshot automatically. For a requested evidence artifact, save the returned screenshot without changing its pixels:

```javascript
var state = await sky.get_window_state({ window: noteWindow, include_screenshot: true });
// After reviewing the displayed image, save it in the run's evidence directory.
var fs = await import('node:fs/promises');
var shot = state.screenshots[0];
// Use .jpg for data:image/jpeg and .png for data:image/png.
await fs.writeFile(evidencePath, Buffer.from(shot.url.split(',')[1], 'base64'));
```

Save selected, reviewed screenshots under `docs/screenshots/<feature>/` when committing evidence. Keep discovery files, credentials, user-profile exports, incidental personal content, and raw microphone recordings out of Git. Link the committed screenshot in the PR using the head commit's GitHub blob URL with `?raw=true`. Record exactly what the image proves, the model, test route, and any existing text correction. A screenshot is point-in-time evidence, not a substitute for automated tests.

See [AssemblyAI acceptance evidence](screenshots/assemblyai/README.md) for the first completed run.
