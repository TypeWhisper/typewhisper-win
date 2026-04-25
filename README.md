# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for Windows. Dictate anywhere, transcribe files, and transform text with reusable workflows. Use local models when privacy matters, or add cloud providers through plugins when speed and scale matter more.

TypeWhisper for Windows includes system-wide dictation, file transcription, workflows, history, dictionary, snippets, local and cloud transcription engines, and bundled integrations. Advanced surfaces like the HTTP API, CLI, plugin SDK, marketplace, and action plugins remain available for power users and automation.

## What's New

- **Workflows:** Prompt actions and matching rules live in one workflow surface with templates for cleanup, translation, email replies, meeting notes, checklists, JSON extraction, summaries, and custom prompts.
- **Expanded engines:** Local SherpaOnnx, whisper.cpp, Granite Speech, and compatible server-based engines sit alongside cloud transcription providers.
- **Streaming preview:** The recording overlay can show partial results while speech is still being captured.
- **Automation refresh:** The local HTTP API and CLI support per-request engine/model overrides, language hints, translation targets, and dictionary term management.
- **Plugin marketplace:** Browse, install, upgrade, and remove bundled or external plugins from Settings.
- **Release channels:** Velopack powers stable, release-candidate, and daily build delivery.

## Features

### Transcription

- **On-device models:** Parakeet TDT 0.6B, Canary 180M Flash, whisper.cpp models, and Granite Speech run locally through plugins. Recommended local models run on CPU, with no GPU required.
- **Cloud transcription:** Groq Whisper, OpenAI Whisper, AssemblyAI, Deepgram, ElevenLabs, Gladia, Google Cloud STT, Soniox, Speechmatics, Cloudflare ASR, Voxtral, and any OpenAI-compatible server can be added through plugins.
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
- **LLM providers:** Groq, OpenAI, Gemini, Claude, Cerebras, Cohere, Fireworks, OpenRouter, OpenAI Compatible, and local Gemma can be used through plugins.
- **Custom prompts:** Add fine-tuning instructions per workflow, or use custom workflow prompts when the built-in templates are not specific enough.
- **Translation:** Cloud LLM translation can fall back to local Marian ONNX translation. Supported target languages include EN, DE, FR, ES, IT, NL, PL, SV, DA, FI, CS, RU, UK, HU, JA, ZH, AR, HI, VI, and ID.

### Personalization

- **Workflow triggers:** Match by process name, website pattern, or hotkey for language, task, model, whisper mode, prompt processing, output format, and action routing.
- **Dictionary:** Custom term corrections fix names, jargon, and recurring misrecognitions. Includes regex support and built-in term packs for developer, medical, legal, finance, and creative domains.
- **Snippets:** Text shortcuts with trigger -> replacement. Placeholders include `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, and `{year}`. Date/time placeholders support custom formats, such as `{date:dd.MM.yyyy}`.
- **History:** Searchable transcription history with raw/final text tracking, app context, inline editing, export, retention controls, and recent-transcription access.

### Integration & Extensibility

- **Plugin system:** Extend TypeWhisper with custom transcription engines, LLM providers, post-processors, memory providers, TTS providers, event observers, and action plugins.
- **Plugin marketplace:** Browse, install, upgrade, and remove plugins directly from Settings. Recommended extensions can be installed automatically on first run.
- **Action plugins:** Linear, Obsidian, Script, Webhook, and LiveTranscript can turn transcriptions into issues, notes, scripts, notifications, or live windows.
- **HTTP API:** Local REST server for status, models, transcription, history, dictionary terms, and dictation control.
- **CLI tool:** Shell-friendly transcription via the bundled `typewhisper` command.

### General

- **Fluent Design:** WPF-UI with Mica backdrop, native title bar, and Fluent controls.
- **Dynamic Island overlay:** Configurable widgets for LED, timer, waveform, active workflow, and microphone level.
- **Home dashboard:** Usage statistics, words per minute, app activity, time saved, and onboarding.
- **Welcome wizard:** Guided setup for extension installation, model download, microphone test, and hotkeys.
- **Release channels:** Velopack-powered delivery for stable, release-candidate, and daily builds.
- **Windows autostart:** Optional start with Windows.
- **System tray:** Minimizes to tray with quick access.
- **Localization:** English, German, French, Spanish, Italian, Portuguese, Dutch, Polish, Czech, Swedish, Danish, and Finnish.

## Install

### Direct Download

Download the latest installer from [GitHub Releases](https://github.com/TypeWhisper/typewhisper-win/releases/latest).

Stable releases use the default Velopack channel. Release candidates and daily builds are published as prereleases on their own update channels. Installed builds can switch channels in Settings.

## Quick Start

1. Install TypeWhisper from the latest Windows release.
2. Open Settings and grant microphone access if Windows asks for it.
3. Pick a transcription engine and, if needed, download a local model.
4. Set a global hotkey or create a workflow-specific hotkey.
5. Trigger dictation and complete your first transcription.

## System Requirements

- Windows 10 or later (x64 or ARM64)
- 8 GB RAM minimum, 16 GB+ recommended for larger local models
- Around 700 MB disk space for Parakeet, around 200 MB for Canary, more for whisper.cpp or Granite Speech models
- .NET 10 SDK for building from source

## Model Recommendations

| Use Case | Recommended Models |
|----------|--------------------|
| Fast general dictation | Parakeet TDT 0.6B, whisper.cpp Base Q5_0 |
| Multilingual dictation with translation | Canary 180M Flash, whisper.cpp Large V3 Turbo |
| Lowest disk usage | whisper.cpp Tiny Q5_0 |
| Higher local accuracy | whisper.cpp Small or Large V3 Turbo |
| Experimental local speech | IBM Granite 4.0 1B Speech |

Local models are provided by bundled plugins and can be installed from the built-in marketplace.

## Build

1. Clone the repository:
   ```bash
   git clone https://github.com/TypeWhisper/typewhisper-win.git
   cd typewhisper-win
   ```

2. Build with .NET 10:
   ```bash
   dotnet build TypeWhisper.slnx
   ```

3. Run the app:
   ```bash
   dotnet run --project src/TypeWhisper.Windows
   ```

4. Run the automated checks before shipping changes:
   ```bash
   dotnet test TypeWhisper.slnx
   ```

The app appears in the system tray, and the welcome wizard guides you through extension installation, model download, and setup.

## HTTP API

The HTTP API is an advanced local automation surface. It binds to `localhost` and `127.0.0.1`, is configurable in Settings, and uses port `8978` by default.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status, active engine, active model, API version, and capability flags |
| `/v1/models` | GET | List all available local and cloud models |
| `/v1/transcribe` | POST | Transcribe multipart or raw audio |
| `/v1/history` | GET | Search history with pagination |
| `/v1/history` | DELETE | Delete a history entry by ID |
| `/v1/dictation/start` | POST | Start recording |
| `/v1/dictation/stop` | POST | Stop recording |
| `/v1/dictation/status` | GET | Check current dictation state |
| `/v1/dictation/transcription` | GET | Poll dictation result by session ID |
| `/v1/dictionary/terms` | GET | List enabled dictionary terms |
| `/v1/dictionary/terms` | PUT | Replace or append dictionary terms |
| `/v1/dictionary/terms` | DELETE | Clear dictionary terms |

`/v1/transcribe` accepts `multipart/form-data` with a `file` part or a raw audio request body. Multipart fields are:

- `language`: exact source language, such as `en` or `de`
- `language_hint`: repeatable language hints; do not combine with `language`
- `task`: `transcribe` or `translate`
- `target_language`: translate the final text to this language
- `response_format`: `json` or `verbose_json`
- `prompt`: request-specific transcription prompt/context
- `engine` and `model`: per-request overrides using IDs from `/v1/models`

Raw audio requests can pass the same options with headers: `X-Language`, `X-Language-Hints`, `X-Task`, `X-Target-Language`, `X-Response-Format`, `X-Prompt`, `X-Engine`, and `X-Model`. Add `?await_download=1` to wait for a local model download or restore when supported.

```bash
curl -X POST http://localhost:8978/v1/transcribe \
  -F "file=@recording.wav" \
  -F "language_hint=de" \
  -F "language_hint=en" \
  -F "response_format=verbose_json"

curl -X POST http://localhost:8978/v1/dictation/start
curl -X POST http://localhost:8978/v1/dictation/stop
curl "http://localhost:8978/v1/dictation/transcription?id=<session-id>"
```

## CLI Tool

The optional `typewhisper` CLI talks to the local HTTP API and is intended for scripts, terminals, and batch workflows.

### Commands

```bash
typewhisper status
typewhisper models
typewhisper transcribe recording.wav --language de --json
typewhisper transcribe recording.wav --language-hint de --language-hint en
typewhisper transcribe recording.wav --engine groq --model whisper-large-v3-turbo
typewhisper transcribe - < audio.wav
```

### Options

| Option | Description |
|--------|-------------|
| `--port <N>` | Server port (default: `8978`) |
| `--json` | Output as JSON |
| `--language <code>` | Source language, such as `en` or `de` |
| `--language-hint <code>` | Repeatable language hint for restricted auto-detection |
| `--task <task>` | `transcribe` (default) or `translate` |
| `--translate-to <code>` | Target language for translation |
| `--engine <id>` | Override the transcription engine |
| `--model <id>` | Override the transcription model |
| `--await-download` | Wait for local model restore/download |

The CLI requires the API server to be running in Settings.

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

TypeWhisper supports plugins for adding custom transcription engines, LLM providers, post-processors, memory providers, TTS providers, event observers, and action plugins. Plugins are .NET class libraries with a `manifest.json`, installed to `%LocalAppData%\TypeWhisper\Plugins\`.

Bundled plugin families include:

| Type | Plugins |
|------|---------|
| Local transcription | SherpaOnnx, whisper.cpp, Granite Speech |
| Cloud transcription | OpenAI, Groq, AssemblyAI, Deepgram, ElevenLabs, Gladia, Google Cloud STT, Soniox, Speechmatics, Cloudflare ASR, Voxtral, OpenAI Compatible |
| Server-backed transcription | Qwen3 STT |
| LLM providers | OpenAI, Groq, Gemini, Claude, Cerebras, Cohere, Fireworks, OpenRouter, OpenAI Compatible, Gemma Local |
| Actions | Linear, Obsidian, Script, LiveTranscript, Webhook |
| Memory | File Memory, OpenAI Vector Memory |

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
|   |-- TypeWhisper.Core/           # Core logic, models, interfaces, persistence
|   |-- TypeWhisper.PluginSDK/      # Plugin contracts, helpers, manifest models
|   |-- TypeWhisper.Cli/            # typewhisper command-line client
|   `-- TypeWhisper.Windows/        # WPF UI, services, view models, app composition
|-- plugins/                        # Bundled plugin source
|-- tests/
|   |-- TypeWhisper.Core.Tests/     # Core xUnit tests
|   `-- TypeWhisper.PluginSystem.Tests/
`-- docs/                           # Design notes and implementation plans
```

**Patterns:** MVVM with CommunityToolkit.Mvvm. `App.xaml.cs` is the composition root. SQLite is used for persistence with a custom migration pattern. Plugins load through AssemblyLoadContext isolation and manifest-based discovery. Workflows drive app/website/hotkey matching and prompt orchestration.

**Key dependencies:** NAudio (audio), NHotkey.Wpf (hotkeys), WPF-UI (Fluent Design), Microsoft.ML.OnnxRuntime (local translation), Velopack (updates), H.NotifyIcon.Wpf (system tray), and org.k2fsa.sherpa.onnx (local ASR).

## License

GPLv3. See [LICENSE](LICENSE) for details. Commercial licensing is available under [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). See [TRADEMARK.md](TRADEMARK.md) for the trademark policy.
