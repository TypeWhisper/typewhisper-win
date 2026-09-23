# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for Windows. Dictate anywhere, transcribe files, and transform text with reusable workflows. Use local models when privacy matters, or add cloud providers through plugins when speed and scale matter more.

TypeWhisper for Windows includes system-wide dictation, file transcription, workflows, history, dictionary, snippets, local and cloud transcription engines, and portable integrations. Advanced surfaces like the HTTP API, CLI, plugin SDK, marketplace, and action plugins remain available for power users and automation.

See [Platform parity and exclusions](docs/PLATFORM_PARITY.md) for the Windows support matrix, Windows-native replacements, and Apple-only non-goals.

The application is built with WinUI and published as `TypeWhisper.exe`. All portable plugin projects, manifests and provider tests live in `plugins/`. Historical release notes remain available as records of earlier versions.

## What's New

- **Workflows:** Prompt actions and matching rules live in one workflow surface with templates for cleanup, translation, email replies, meeting notes, checklists, JSON extraction, summaries, and custom prompts.
- **Expanded engines:** Local SherpaOnnx, whisper.cpp, Qwen3 Local, and compatible server-based engines sit alongside cloud transcription providers.
- **Streaming preview:** The recording overlay can show partial results while speech is still being captured.
- **Automation refresh:** The local HTTP API and CLI support tokenized discovery, local-file transcription, workflow/rule listing, per-request engine/model overrides, language hints, translation targets, and dictionary term/correction management.
- **Plugin marketplace:** Browse, install, upgrade, and remove bundled or external plugins from Settings.
- **Release channels:** Velopack powers stable, release-candidate, and daily build delivery.

## Features

### Transcription

- **On-device models:** Parakeet TDT 0.6B, Canary 180M Flash, whisper.cpp models, and Qwen3 Local run locally through plugins. Recommended local models run on CPU, with no GPU required.
- **Cloud transcription:** Groq Whisper, OpenAI Whisper, AssemblyAI, Deepgram, ElevenLabs, Reson8, Gladia, Soniox, Speechmatics, Cloudflare ASR, Voxtral, and any OpenAI-compatible server can be added through plugins.
- **Streaming preview:** Silero VAD detects speech segments during recording and shows partial transcription results in the overlay before recording stops.
- **Short-clip handling:** Brief utterances are padded and retained more reliably across local and cloud engines.
- **File transcription:** Drag and drop audio/video files. Supports WAV, MP3, M4A, AAC, OGG, FLAC, WMA, MP4, MKV, AVI, MOV, and WebM.
- **Subtitle export:** Export transcriptions as TXT, SRT, or WebVTT.

### Dictation

- **System-wide dictation:** Hybrid, Toggle-only, Hold-only, and workflow-specific hotkeys can paste text into any app.
- **Non-blocking pipeline:** Queue multiple recordings while transcription runs in the background.
- **Sound feedback:** Audio cues for recording start and stop.
- **Silence detection:** Automatically stops recording after a configurable silence period.
- **Whisper mode:** Boosted microphone gain for quiet speech.
- **Audio normalization:** Automatic gain control for consistent input levels.
- **Media pause:** Automatically pauses media playback during recording.
- **Audio ducking:** Reduces system volume while recording.

### AI Processing

- **Workflows:** Build reusable transformations for cleanup, translation, rewriting, extraction, formatting, and app-specific automation. Workflows can run by app, website, or dedicated hotkey.
- **LLM providers:** Groq, OpenAI, Gemini, Claude, Cerebras, Cohere, Fireworks, OpenRouter, OpenAI Compatible, Mistral, and GitHub Copilot can be used through plugins.
- **Custom prompts:** Add fine-tuning instructions per workflow, or use custom workflow prompts when the built-in templates are not specific enough.
- **Translation:** Cloud LLM translation can fall back to local Marian ONNX translation. Supported target languages include EN, DE, FR, ES, IT, NL, PL, SV, DA, FI, CS, RU, UK, HU, JA, ZH, AR, HI, VI, and ID.

### Personalization

- **Workflow triggers:** Match by process name, website pattern, or hotkey for language, task, model, whisper mode, prompt processing, output format, and action routing.
- **Dictionary:** Custom term corrections fix names, jargon, and recurring misrecognitions with literal phrase matching, optional case sensitivity, and built-in term packs for developer, medical, legal, finance, and creative domains. Replacement text supports `\s`, `\n`, `\r`, `\t`, and `\\` escapes.
- **Snippets:** Text shortcuts with trigger -> replacement. Placeholders include `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, and `{year}`. Date/time placeholders support custom formats, such as `{date:dd.MM.yyyy}`.
- **History:** Searchable transcription history with raw/final text tracking, app context, inline editing, export, retention controls, and recent-transcription access.

### Integration & Extensibility

- **Plugin system:** Extend TypeWhisper with custom transcription engines, LLM providers, post-processors, memory providers, TTS providers, event observers, and action plugins.
- **Plugin marketplace:** Browse, install, upgrade, and remove plugins directly from Settings. Recommended extensions can be installed automatically on first run.
- **Action plugins:** Portable action providers such as Obsidian can route workflow output to external tools.
- **HTTP API:** Local REST server for status, models, transcription, workflows/rules, history, dictionary terms and corrections, and dictation control.
- **CLI tool:** Shell-friendly transcription via the bundled `typewhisper` command.

### General

- **Fluent Design:** WinUI 3 with Mica backdrop, native title bar, and Fluent controls.
- **Dynamic Island overlay:** Configurable widgets for LED, timer, waveform, active workflow, and microphone level.
- **Quick Launch:** Access dictation, history, workflows and settings from the WinUI launcher.
- **Welcome wizard:** Guided setup for extension installation, model download, microphone test, and hotkeys.
- **Release channels:** Velopack-powered delivery for stable, release-candidate, and daily builds.
- **Windows autostart:** Optional start with Windows.
- **System tray:** Minimizes to tray with quick access.

## Install

Use a WinUI Daily installer from [GitHub Releases](https://github.com/TypeWhisper/typewhisper-win/releases), or build the current source. Earlier WPF installers are historical releases and do not represent this branch.

WinUI packages use their own installation identity and update channels. See the [Daily candidate guide](docs/DAILY-1.1-CANDIDATE.md) for runtime requirements, data import and release acceptance.

## Quick Start

1. Install a WinUI build or build the current source.
2. Open Settings and grant microphone access if Windows asks for it.
3. Pick a transcription engine and, if needed, download a local model.
4. Set a global hotkey or create a workflow-specific hotkey.
5. Trigger dictation and complete your first transcription.

## System Requirements

- Windows 10/11, with a configured minimum build of 19041 (x64 or ARM64). Native validation on older Windows builds and ARM64 hardware is still required before claiming full compatibility.
- 8 GB RAM minimum, 16 GB+ recommended for larger local models
- Around 700 MB disk space for Parakeet, around 200 MB for Canary, more for whisper.cpp or Qwen3 Local models
- .NET 10 SDK for building from source

## Model Recommendations

| Use Case | Recommended Models |
|----------|--------------------|
| Fast general dictation | Parakeet TDT 0.6B, whisper.cpp Base Q5_0 |
| Multilingual dictation with translation | Canary 180M Flash, whisper.cpp Large V3 Turbo |
| Lowest disk usage | whisper.cpp Tiny Q5_0 |
| Higher local accuracy | whisper.cpp Small or Large V3 Turbo |

Local models are provided by bundled plugins and can be installed from the built-in marketplace.

For backend compatibility and troubleshooting on AMD GPUs, see [AMD acceleration on Windows](docs/AMD_ACCELERATION.md).

The current Windows evaluation of NVIDIA's streaming Parakeet EOU model is documented in [Parakeet Realtime EOU on Windows](docs/PARAKEET_REALTIME_EOU_WINDOWS.md).

## Build

Install the .NET 10 SDK and Windows SDK 10.0.26100 or later on Windows. The only application project is `src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj`.

```powershell
dotnet build TypeWhisper.slnx
dotnet publish src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
```

Run `publish/win-x64/TypeWhisper.exe`. Use `win-arm64` for ARM64 builds. The publish output includes the application resources and bundled CLI.

On Marco's development machine, build and launch the current checkout with the shared development script:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```

It detects WinUI-only checkouts and publishes to the stable development output directory. Do not start binaries directly from a temporary worktree.

Run the automated suites without a desktop, microphone, downloaded models or provider credentials:

```powershell
./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

GitHub Actions contains six workflows for the WinUI application and its portable plugins:

| Workflow | Purpose |
|----------|---------|
| CI | Build the WinUI solution on Windows and run headless tests on Windows and Linux for pull requests and main pushes. |
| Candidate | Validate x64 and ARM64 candidates; publish Daily prereleases on the daily schedule or an explicit main dispatch with `publish_daily=true`. |
| Packaging | Validate x64 and ARM64 installers and portable packages without publishing. |
| Plugins | Validate plugin metadata and build changed portable plugins; manual runs check all plugins. |
| Security | Review dependencies and audit NuGet and Python packages. |
| Store | Build WinUI MSIX packages manually for x64 and ARM64. |

Workflow filenames remain stable to preserve their Actions history and the Candidate version counter. Legacy application and plugin release workflows are retired; existing public packages are unchanged.

## HTTP API

Use the [WinUI HTTP API reference](docs/WINUI-HTTP-API.md) for authentication, discovery, transcription, dictation, models, history, workflows and backups. The API is controlled from Settings > Advanced.

## CLI Tool

In Windows 1.1, install the optional `typewhisper` command from **Settings > Advanced > Command Line Tool**, enable the HTTP API, and open a new terminal. The CLI discovers the installing app's profile, port, and authentication settings automatically.

```powershell
typewhisper status
typewhisper models
typewhisper transcribe recording.wav --language de --json
typewhisper history search "meeting notes"
typewhisper last
```

See the [Windows 1.1 CLI guide](docs/WINDOWS-CLI.md) for installation, profile overrides, dictation, model management, settings backups, and current limits.

## Workflows

Workflows let you configure transcription, transformation, and automation behavior per application, website, or hotkey. For example:

- **Outlook:** German dictation, email reply template, auto-submit enabled
- **Slack:** English cleanup workflow with Groq or OpenAI prompt processing
- **Terminal:** Whisper mode enabled with a command-focused dictionary
- **github.com:** English cleanup workflow that matches in any browser
- **docs.google.com:** German dictation workflow that translates to English

Create workflows in Settings > Workflows. Choose a template, assign an app, website, or hotkey trigger, then configure language/task/model overrides, prompt processing, output behavior, action routing, and priority. Website patterns support wildcard matching, so `*.github.com` matches `gist.github.com`.

When you start dictating, TypeWhisper matches the active window and browser URL against enabled workflows with the following priority:

1. **Website match:** browser URL patterns, such as `github.com` in any supported browser
2. **App match:** process names, such as `OUTLOOK.EXE` or `Code.exe`
3. **Hotkey match:** workflow-specific hotkeys that force a selected workflow

The active workflow is shown in the recording overlay.

## Plugins

Plugins are portable .NET class libraries with a `manifest.json`. WinUI renders their settings through the portable SDK and installs them in its own profile through the v2 marketplace. It does not load WPF settings assemblies.

See [plugin package documentation](docs/PLUGIN-PACKAGES-1.1.md) for package layout, lifecycle and host capabilities. All providers and the internal CTC component share the `plugins/` source directory. WPF-only providers without a portable implementation are no longer included.

### Plugin Types

| Interface | Purpose |
|-----------|---------|
| `ITranscriptionEnginePlugin` | Local, cloud, or custom speech-to-text engines |
| `ILlmProviderPlugin` | LLM chat completions for workflow processing |
| `IPostProcessorPlugin` | Post-processing pipeline for cleanup and formatting |
| `IActionPlugin` | Custom actions triggered by workflow output |
| `ITypeWhisperPlugin` | Event observer, lifecycle hook, or utility plugin |

### SDK Helpers

The SDK includes helpers for OpenAI-compatible APIs:

- `OpenAiTranscriptionHelper`: multipart/form-data upload for Whisper-compatible endpoints
- `OpenAiChatHelper`: chat completion requests
- `OpenAiApiHelper`: shared HTTP error handling

## Architecture

```text
typewhisper-win/
|-- src/
|   |-- TypeWhisper.Core/           # Core logic, models and persistence
|   |-- TypeWhisper.PluginSDK/      # Portable plugin contracts and helpers
|   |-- TypeWhisper.PluginHost/     # Isolated plugin loading and package lifecycle
|   |-- TypeWhisper.Presentation/   # Application logic independent of the UI
|   |-- TypeWhisper.Cli/            # typewhisper command-line client
|   `-- TypeWhisper.WinUI/          # WinUI UI, platform services and app composition
|-- plugins/                       # Portable providers, tests and internal CTC component
|-- tests/                         # Core, host, presentation, CLI and native tests
`-- docs/                          # Guides and historical design/release records
```

**Key dependencies:** Windows App SDK / WinUI 3, NAudio (audio), CommunityToolkit.Mvvm, Velopack (updates), H.NotifyIcon.WinUI (system tray), and plugin-owned native inference runtimes.

## License

GPLv3. See [LICENSE](LICENSE) for details. Commercial licensing is available under [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). See [TRADEMARK.md](TRADEMARK.md) for the trademark policy.
