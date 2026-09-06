# Testing the 1.1 application

Current implementation checkpoint: `ff77f43a` (2026-09-06). Functional coverage and outstanding features are tracked in the [Windows and Mac comparison](WINUI-FUNCTIONAL-STATUS.md); passing tests do not imply all displayed features are implemented.

## Required headless checks

```powershell
& ./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

The runner executes portable host, Presentation and discovered `plugins/*/Tests/*.csproj` suites sequentially and writes TRX files plus `summary.json` under `artifacts/test-results/winui-headless`. No desktop, Computer Use, real microphone, downloaded models or provider credentials are required.

[CI workflow](../.github/workflows/winui-headless.yml) runs on Windows and Ubuntu. At [run 34060774341](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060774341), verified from logs:

| Suite | Windows passed / skipped | Ubuntu passed / skipped |
|---|---|---|
| Portable SDK / host | 112 / 0 | 111 / 0 |
| Presentation | 96 / 0 | 96 / 0 |
| Groq plugin | 29 / 0 | 29 / 0 |
| NVIDIA/Sherpa plugin | 25 / 1 | 21 / 5 |
| Total | **262 / 1** | **257 / 5** |

The DPAPI test is compiled on Windows only. Five CUDA-specific cases require Windows x64; the complementary unsupported-platform test runs elsewhere. These tests do not install a real GPU runtime. [CodeQL run 34060775205](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060775205) passed. CodeRabbit skipped the draft review; its green status does not establish review completion.

## What the tests establish

- Metadata/identity/compatibility inspection, portable plugin activation/settings, PCM result and token-timing contracts, lifecycle failure/cancellation and disposal behavior using isolated fixtures.
- Catalog/package parsing, HTTPS/size/SHA-256 validation, unsafe archive rejection, installed-index persistence, staged update/restart and uninstall/data retention. Actual package fixture hooks exercise progress and failed operations; no provider owner's installation is removed.
- Model selection/persistence/rollback, activation retry, download progress/cancel, HTTP failures, incomplete responses and temporary-file cleanup through controlled dependencies.
- Real Groq provider multipart/WAV construction, model/language forwarding, auto-language omission, headers, auth/rate-limit/server failures, retry and cancellation through an injected HTTP handler. Windows separately verifies DPAPI persistence/corruption/removal with synthetic secrets.
- History load/search/projection, clipboard coordination through injected platform services, hotkey state transitions, preview cancellation, audio-effects/silence decisions and dictionary/snippet snapshots/persistence. Provider-first selection covers unavailable providers, missing models and fallback within a provider only.
- Managed CTC scoring/timing decisions, serialized activation/draining/stale-result handling and automatic parent-dependent enablement. These establish logic, not recognition quality.

These suites do not compile/render the full WinUI app, transcribe real cloud audio, validate microphone/keyboard drivers, publish the catalog or prove editor paste consumption. The old WPF/Core test suites are not equivalent to WinUI end-to-end coverage merely because source is shared.

## Native builds and inference

Local app builds, launches and UI smoke tests must use:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run --winui <checkout>
```

Use the current checkout/worktree, not a transient build output or the installed production app. Output is `F:/typewhisper/dev-output/typewhisper-win/WinUI`.

Optional native inference is documented in [audio tests](../tests/TypeWhisper.Dictation.AudioTests/README.md) and [CTC probe](../tests/TypeWhisper.ParakeetCtc.Probe/README.md). These load published plugin folders and existing isolated weights. They are separate from headless CI and require explicit environment configuration; do not download models or use credentials just to run the default suite.

## Existing bounded native evidence

These are earlier implementation-session checks, not rerun during the documentation audit:

- The prescribed development build/launch passed. Owner feedback confirmed immediate dictation, hybrid hold/tap, whole-text insertion and plugin toggle behavior.
- Published Parakeet/Canary generated-speech inference passed, including model/language metadata. Canary downloaded anonymously and selection survived restart. UI cancellation was not confirmed before completion; cancellation evidence comes from headless tests.
- CTC produced finite emissions for official English fixture audio, accepted a positive correction and rejected the inverse hint through the published portable package. Whole-utterance fixture timings do not prove real TDT/microphone alignment. Broader German, names, vocabulary, false positives and FluidAudio equivalence remain unverified.
- Earlier timing, adaptive candidate/scoring and diagnostics regressions are covered by the managed/native fixtures. Development diagnostics are bounded/rotating and record alignment/scoring metadata, not additional audio or transcript text. Per-term threshold UI and real vocabulary success need their own acceptance evidence.
- Earlier enable/disable, keyboard focus and restart checks predate the final internal-CTC presentation. Current headless tests enforce parent-controlled CTC; do not reproduce the removed standalone toggle as today's UI.
- Synthetic Groq key editing, encrypted save/enable/removal passed during setup development. Later Installed/Discover and provider-settings navigation checks showed the two actual integrations. A configured/ready key is not evidence of a successful live Groq transcription.
- The uninstall dialog was inspected and canceled. Actual uninstall/reinstall mutations use isolated package fixtures. The last live Discover check returned HTTP 404 for `plugins-v2.json`; published-feed acceptance remains open.

No personal keys, recordings or weights belong in test fixtures, logs, commits or screenshots. The documentation audit changes no application state.

## Outstanding acceptance gates

- [ ] Real local and Groq dictation through install/configure/select/record/insert/history, with accurate metadata, failure/cancel and provider switch-back.
- [ ] Published v2 install/use/update/restart/uninstall/reinstall, including retained configuration/models and failed hook/update recovery.
- [ ] Real microphone changes, unplug/replug, sleep/resume, sound devices, media/ducking restoration and short quiet utterances.
- [ ] Rich clipboard formats, focus changes within/across windows, editor consumption, modifier races and review-first behavior once wired.
- [ ] Durable recovery, history mutations/retention/audio, real files/recorder/workflows and all remaining data services as they are integrated.
- [ ] Full keyboard/screen-reader/high-contrast, actual 200% DPI and mixed-monitor transitions. Logical viewport previews are not OS DPI tests.
- [ ] Actual WinUI release build/installer/update/rollback and each claimed OS/architecture; headless CI is not a WinUI build gate yet.
- [ ] Deferred controlled legacy-versus-WinUI benchmark for PR #447: same audio/model/backend/thread settings, separate warm-up and steady state, capture onset, insertion latency, memory and explicit uncertainty.

For each new feature, test observable state and failure/race outcomes behind portable boundaries first, then validate the Windows adapter separately. Avoid tests that only repeat a UI implementation or use simulated output as transcription evidence.
