# Testing the 1.1 application

Version 1.1 is greenfield. Existing provider logic can be reused, while SDK and application boundaries should make behavior independently testable. Compatibility with old plugin binaries is not a design requirement.

## Required headless checks

Run the same command locally and in CI:

```powershell
./eng/Test-WinUIHeadless.ps1
```

This builds and runs the portable plugin-host and Presentation suites in Release mode. It does not launch TypeWhisper, use Computer Use/UI Automation, open a microphone, load model weights, request provider credentials, or call transcription services. NuGet restore may access the package registry. Tests create isolated temporary files where persistence is part of the behavior under test.

Results are written to `artifacts/test-results/winui-headless/`: one TRX file per suite and `summary.json` with each exit code and elapsed time. Both suites run even if one fails; the script returns failure if either suite fails. Override the path with `-ResultsDirectory`.

`.github/workflows/winui-headless.yml` runs this command on Windows and Linux and uploads the results even on failure. The first remote Linux/Windows matrix execution remains pending until the branch is pushed and CI runs.

## What the checks establish

- `PluginManagementController` is used by the actual WinUI Plugins view. Tests supply a package inventory and runtime bindings to exercise state projection, recording/processing guards, duplicate actions, activation failure/retry, invalid package routing and out-of-order refreshes without loading native code.
- `VocabularyPluginSession` tests use controlled asynchronous completions to verify activation/disable/disposal ordering, draining native work, cancellation and rejection of stale results. They do not rely on arbitrary sleep durations.
- `VocabularyHostServices` persistence tests reopen the same isolated profile and check enablement, preserved settings, malformed files and failed writes. Asset paths are separate from settings, and these hosts disable production-data migration.
- Inventory tests inspect actual fixture assembly metadata without resolving or activating the entry point. CTC scoring/token-timing tests exercise managed math and validation without downloading models.
- Presentation tests cover history, clipboard coordination via injected platform services, hotkey state machines, live-preview cancellation, audio-effects/silence logic, and dictionary/snippet snapshots and persistence.

When adding a provider or feature, place its decisions and state transitions behind portable interfaces first. Test observable outcomes, failed operations and races at that boundary; keep WinUI event handlers responsible for rendering state and forwarding actions.

## Optional native and UI acceptance

Local model inference is a separate opt-in test: `PortableParakeetTests` in `tests/TypeWhisper.Dictation.AudioTests` loads a published plugin and existing model weights, transcribes locally generated speech and checks CTC token intervals and unload behavior. See that project's README for the required environment variables. A successful native test does not establish real microphone or keyboard-hook behavior.

Native application builds and launches continue through `F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run --winui <checkout>`. UI interaction, screenshots, keyboard focus and DPI require a separate local Windows acceptance pass. They supplement the headless checks and do not block CI on Computer Use availability.

Current local evidence: 126 portable plugin-host tests (including one Windows-only DPAPI case) and 90 Presentation tests passed in Release mode. Linux omits the Windows-only case. The actual CTC service is tested with injected plugin leases and an isolated profile, including reload of enabled/disabled state and failed activation followed by retry. A published sherpa-onnx PCM inference test passed separately.

Local Computer Use acceptance verified CTC enable/disable with Enter and Space, focus restoration after asynchronous loading/unloading, and enabled/disabled state after real application restarts. The owner separately confirmed a manual plugin toggle cycle. These checks do not establish comprehensive screen-reader or DPI coverage. Plugins is now the only CTC enablement control; the separate Dictation settings switch was removed at the owner's request. Enabled CTC is applied automatically during dictation.

Model management: 14 added headless cases cover missing models, activation failure/retry, selection persistence and rollback, download progress/cancel/retry, language persistence/forwarding, HTTP failures, incomplete responses and temporary-file cleanup. Published Parakeet and Canary packages passed two native generated-speech inference cases, including catalog language metadata. The real Canary download completed without a Hugging Face token; plugin settings loaded Canary successfully and displayed the language tooltip. The UI cancel click did not establish a confirmed cancellation before the download completed; cancellation evidence comes from the headless tests.
The local plugin is presented as “Lokale Modelle”; NVIDIA is shown as the model publisher, while sherpa-onnx remains an internal provider identifier. Language lists are supplied by the plugin metadata (Parakeet's list follows its NVIDIA model card). A real app restart restored the Canary selection, Dictation showed the same model, and selecting Parakeet from Dictation restored the original model. No Hugging Face token UI was added: the public download completed anonymously.

## Groq cloud transcription

The portable suite now includes the existing Groq provider regressions plus cloud runtime tests. A controlled HTTP handler verifies the real provider's multipart requests, WAV encoding, model/language forwarding, automatic-language omission, API-key headers, authentication/rate-limit/server failures, retry, cancellation, concurrent-operation rejection and persisted configuration after restart. No request reaches Groq during these checks. Windows additionally verifies real DPAPI encryption, reload, corruption recovery and removal using synthetic credentials in a temporary directory.

The package-load regression uses the actual WinUI host version, now 1.1.0. Native acceptance caught the earlier 0.0.1 value rejecting Groq's minimum host version; this was corrected. The redesigned plugin settings keep the masked key field editable before enablement, and Save & enable performs setup in one operation. Actual UI typing, encrypted save, automatic enablement and removal passed with a synthetic key. The key was removed afterwards, and Parakeet remained selected. The published application was built/launched through the prescribed dev script.

A live Groq transcription with a user-provided key remains unverified. Save a key in Plugins > Groq > Settings, optionally Check connection (GET /models, no audio), then choose Use model. Confirm one completed dictation is sent to Groq, inserted and recorded with engine `groq` and the selected model. Cloud mode does not send live-preview requests. Check switching back to a local model separately. Automated HTTP tests do not establish real network, billing or service availability.

API-key entry is restricted to Plugins > Groq > Settings at the owner's request. The duplicate Groq configuration section was removed from the global Models page.

Seven history display cases cover Parakeet, Canary, both Groq Whisper models, unknown providers/models and missing model metadata. The global Models page has been removed from navigation and settings search. Dictation selects local and cloud models; model downloads and API-key entry remain in plugin settings.

CTC dependency enablement tests now verify that the parent provider state overrides old standalone preferences without writing an independent enablement flag. The two visible plugins are NVIDIA Parakeet and Groq; CTC errors are surfaced through NVIDIA Parakeet. The full headless suite passes 126 plugin-host and 90 Presentation cases.

Provider-first selection: six cases verify disabled/unconfigured providers, missing downloads, an empty catalog, restoration of the provider’s last ready model and fallback within that provider only. Current suite: 126 plugin-host + 96 Presentation tests.

Native UI acceptance verified the Installed/Discover round trip, the two real provider entries, NVIDIA-only model choices, the disabled Groq setup state, direct navigation to Groq settings and Back to Dictation. No credentials were changed and no cloud request was made during this pass.
