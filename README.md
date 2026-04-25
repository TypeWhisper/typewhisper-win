# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for Windows. Transcribe audio using on-device AI models or cloud APIs via extensions, then process the result with custom LLM prompts. Your voice data stays on your PC with local models — or use cloud APIs for faster processing.

## Features

### Transcription

- **On-device models** — Parakeet TDT 0.6B (25+ languages, fast) and Canary 180M Flash (EN/DE/FR/ES with translation), running on CPU via SherpaOnnx with int8 quantization — no GPU required
- **Cloud transcription** — Groq Whisper, OpenAI Whisper, AssemblyAI, Deepgram, and any OpenAI-compatible server (Ollama, LM Studio, vLLM). API keys encrypted at rest via DPAPI
- **Streaming preview** — Silero VAD detects speech segments during recording and transcribes them in real time, showing partial results in the overlay before recording stops
- **Live transcription** — Floating window with real-time continuous transcription (via LiveTranscript plugin)
- **File transcription** — Drag-and-drop audio/video files. Supports WAV, MP3, M4A, AAC, OGG, FLAC, WMA, MP4, MKV, AVI, MOV, WebM
- **Subtitle export** — Export transcriptions as TXT, SRT, or WebVTT

### Dictation

- **System-wide** — Three independent hotkeys: Hybrid (short press toggles, long press is push-to-talk), Toggle-only, and Hold-only. Per-profile hotkeys for direct profile activation. Auto-pastes into any app
- **Non-blocking pipeline** — Multiple recordings can be queued while transcription runs in the background
- **Sound feedback** — Audio cues for recording start and stop
- **Silence detection** — Automatically stops recording after configurable silence period
- **Whisper mode** — Boosted microphone gain for quiet speech
- **Audio normalization** — Automatic gain control for consistent input levels
- **Media pause** — Automatically pauses media playback during recording
- **Audio ducking** — Reduces system volume while recording

### AI Processing

- **Custom prompts** — Process transcriptions (or any text) with LLM prompts. Standalone Prompt Palette via global hotkey — a floating panel for AI text processing independent of dictation
- **LLM providers** — Groq, OpenAI, Google Gemini, and any OpenAI-compatible server (Ollama, LM Studio, vLLM) — all via plugins
- **Translation** — Cloud LLM translation with local Marian ONNX model fallback. 20 target languages: EN, DE, FR, ES, IT, NL, PL, SV, DA, FI, CS, RU, UK, HU, JA, ZH, AR, HI, VI, ID

### Personalization

- **Profiles** — Per-app and per-website overrides for language, task, transcription model, and whisper mode. Match by process name and/or URL pattern with wildcard support. Automatically activates when dictating in a matched application or website
- **Dictionary** — Custom term corrections applied after transcription (e.g., fix names, jargon, or recurring misrecognitions). Regex support. 13 built-in term packs: Web Dev, .NET/C#, DevOps, Data & AI, Design, Game Dev, Mobile, Security, Databases, Medical, Legal, Finance, Music Production
- **Snippets** — Text shortcuts with trigger→replacement. Placeholders: `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, `{year}`. Date/time support custom formats (e.g. `{date:dd.MM.yyyy}`)
- **History** — Searchable transcription history with raw/final text tracking, app context, inline editing, and export (TXT, CSV, Markdown, JSON)

### Integration & Extensibility

- **Plugin system** — Extensible plugin architecture with SDK and marketplace. Create custom plugins for transcription engines, LLM providers, post-processors, or action plugins
- **Plugin marketplace** — Browse, install, update, and uninstall plugins directly from Settings. Auto-installs recommended extensions on first run
- **Action plugins** — Linear (create issues), Obsidian (create notes), Script (run custom scripts), Webhook (HTTP notifications)
- **HTTP API** — Local REST server for integration with external tools (status, models, transcription, history, profiles, dictation control)
- **Model auto-unload** — Automatically unloads models after configurable idle timeout to save memory

### General

- **Fluent Design** — WPF-UI with Mica backdrop, native title bar, and Fluent controls
- **Dynamic Island overlay** — Configurable widgets (LED, timer, waveform) with multi-monitor support and real-time microphone level meter
- **Home dashboard** — Usage statistics (words, WPM, apps, time saved) with activity chart
- **Welcome wizard** — Guided 4-step onboarding: extension installation & model download, microphone test, hotkey setup. Re-accessible from the dashboard
- **Auto-update** — Built-in updates via Velopack
- **Windows autostart** — Optional start with Windows (via registry)
- **System tray** — Minimizes to tray with quick access
- **Localization** — English, German, French, Spanish, Italian, Portuguese, Dutch, Polish, Czech, Swedish, Danish, Finnish

## System Requirements

- Windows 10 or later (x64 or ARM64)
- 8 GB RAM minimum, 16 GB+ recommended for larger models
- ~700 MB disk space for the Parakeet model, ~200 MB for Canary

## Models

### Local Models

| Use Case | Model | Size |
|----------|-------|------|
| General transcription (25+ languages) | Parakeet TDT 0.6B | 670 MB, int8 |
| Multilingual with translation (EN/DE/FR/ES) | Canary 180M Flash | 198 MB, int8 |

Both models run on CPU with int8 quantization — no GPU required. Local models are provided by the SherpaOnnx plugin, installed via the built-in marketplace.

### Cloud Models (optional)

| Provider | Model | Notes |
|----------|-------|-------|
| Groq | whisper-large-v3 | Fast cloud transcription, supports translation |
| Groq | whisper-large-v3-turbo | Fastest, no translation |
| OpenAI | gpt-4o-transcribe | Highest accuracy |
| OpenAI | gpt-4o-mini-transcribe | Lower cost, good quality |
| OpenAI | whisper-1 | Classic, supports translation |
| AssemblyAI | various | Real-time and batch transcription |
| Deepgram | nova-3 | Fast, accurate cloud ASR |
| OpenAI Compatible | Any model | Local LLM servers (Ollama, LM Studio, vLLM) |

Cloud providers are available as plugins and can be configured in Settings > Extensions.

## Build

1. Clone the repository:
   ```bash
   git clone https://github.com/TypeWhisper/typewhisper-win.git
   cd typewhisper-win
   ```

2. Build with .NET 10:
   ```bash
   dotnet build
   ```

3. Run the app:
   ```bash
   dotnet run --project src/TypeWhisper.Windows
   ```

4. The app appears in the system tray — the welcome wizard guides you through extension installation, model download, and setup.

## HTTP API

TypeWhisper can run a local HTTP server (default port 8978, configurable in Settings) for integration with external tools.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status and active model |
| `/v1/models` | GET | List all available models (local + cloud) |
| `/v1/transcribe` | POST | Transcribe multipart or raw audio |
| `/v1/history` | GET | Search history with pagination. Query params: `q`, `limit`, `offset` |
| `/v1/history` | DELETE | Delete history entries by ID |
| `/v1/profiles` | GET | List all profiles |
| `/v1/profiles/toggle` | PUT | Toggle a profile on/off by ID |
| `/v1/dictation/start` | POST | Start recording |
| `/v1/dictation/stop` | POST | Stop recording |
| `/v1/dictation/status` | GET | Check current dictation state |
| `/v1/dictation/transcription` | GET | Poll dictation result by session ID |
| `/v1/dictionary/terms` | GET/PUT/DELETE | List, replace/append, or clear dictionary terms |

`/v1/transcribe` accepts either `multipart/form-data` with a `file` part or a raw audio request body. Multipart fields are:

- `language` - exact source language, such as `en` or `de`
- `language_hint` - repeatable language hints; do not combine with `language`
- `task` - `transcribe` or `translate`
- `target_language` - translate the final text to this language
- `response_format` - `json` or `verbose_json`
- `prompt` - request-specific transcription prompt/context
- `engine` and `model` - per-request overrides using IDs from `/v1/models`

Raw audio requests can pass the same options with headers: `X-Language`, `X-Language-Hints`, `X-Task`, `X-Target-Language`, `X-Response-Format`, `X-Prompt`, `X-Engine`, and `X-Model`. Add `?await_download=1` to wait for a local model download/restore when supported.

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

## CLI

The optional `typewhisper` CLI talks to the local HTTP API.

```bash
typewhisper status
typewhisper models
typewhisper transcribe recording.wav --language de --json
typewhisper transcribe recording.wav --language-hint de --language-hint en
typewhisper transcribe recording.wav --engine groq --model whisper-large-v3-turbo
typewhisper transcribe - < audio.wav
```

Useful flags: `--port`, `--json`, `--language`, repeatable `--language-hint`, `--task`, `--translate-to`, `--engine`, `--model`, and `--await-download`.

## Profiles

Profiles let you configure transcription settings per application or website. For example:

- **Outlook** — German language
- **Slack** — English language
- **Terminal** — Whisper mode always on
- **github.com** — English language (matches in any browser)
- **docs.google.com** — German language, translate to English

Create profiles in Settings > Profiles. Assign process names and/or URL patterns, set language/task/model overrides, and adjust priority. URL patterns support wildcard matching — e.g. `*.github.com` matches `gist.github.com`.

When you start dictating, TypeWhisper matches the active window and browser URL against your profiles with the following priority:
1. **Process + URL match** — highest specificity (e.g. chrome.exe + github.com)
2. **URL-only match** — cross-browser profiles (e.g. github.com in any browser)
3. **Process-only match** — generic app profiles (e.g. all of Chrome)

The active profile is shown in the recording overlay.

## Plugins

TypeWhisper supports plugins for adding custom transcription engines, LLM providers, post-processors, and action plugins. Plugins are .NET class libraries with a `manifest.json`, installed to `%LocalAppData%/TypeWhisper/Plugins/`.

The built-in marketplace provides the following plugins:

| Plugin | Type | Description |
|--------|------|-------------|
| SherpaOnnx | Transcription | Local ASR with Parakeet and Canary models |
| OpenAI | Transcription + LLM | OpenAI Whisper and GPT models |
| Groq | Transcription + LLM | Groq Whisper and Llama models |
| Google Gemini | LLM | Gemini 2.5 Flash, Pro, and Flash Lite |
| AssemblyAI | Transcription | AssemblyAI speech-to-text |
| Deepgram | Transcription | Deepgram Nova ASR |
| ElevenLabs | Transcription | ElevenLabs Scribe speech-to-text |
| OpenAI Compatible | Transcription + LLM | Any OpenAI-compatible server (Ollama, LM Studio, vLLM) |
| Linear | Action | Create Linear issues from transcriptions |
| Obsidian | Action | Create Obsidian notes from transcriptions |
| Script | Action | Run custom scripts with transcription data |
| LiveTranscript | Action | Floating live transcription window |
| Webhook | Event | HTTP notifications for transcription events |

### Plugin Types

| Interface | Purpose |
|-----------|---------|
| `ITranscriptionEnginePlugin` | Cloud/custom transcription (e.g., Whisper API) |
| `ILlmProviderPlugin` | LLM chat completions (e.g., translation, course correction) |
| `IPostProcessorPlugin` | Post-processing pipeline (text cleanup, formatting) |
| `IActionPlugin` | Custom actions triggered by transcription events |
| `ITypeWhisperPlugin` | Event observer (e.g., webhook, logging) |

### SDK Helpers

The SDK includes helpers for OpenAI-compatible APIs:
- `OpenAiTranscriptionHelper` — multipart/form-data upload for Whisper-compatible endpoints
- `OpenAiChatHelper` — chat completion requests
- `OpenAiApiHelper` — shared HTTP error handling

## Architecture

```
typewhisper-win/
├── src/
│   ├── TypeWhisper.Core/           # Core logic (net10.0)
│   │   ├── Data/                   # SQLite database with migrations
│   │   ├── Interfaces/             # Service contracts
│   │   ├── Models/                 # Profile, AppSettings, ModelInfo, TermPack, etc.
│   │   └── Translation/            # MarianTokenizer, MarianConfig (local ONNX translation)
│   ├── TypeWhisper.PluginSDK/      # Plugin SDK (net10.0-windows)
│   │   ├── Helpers/                # OpenAiChatHelper, OpenAiTranscriptionHelper
│   │   └── Models/                 # PluginManifest, PluginEvents, etc.
│   └── TypeWhisper.Windows/        # WPF UI layer (net10.0-windows, WPF-UI Fluent Design)
│       ├── Controls/               # HotkeyRecorderControl, MicLevelMeter
│       ├── Native/                 # P/Invoke, KeyboardHook
│       ├── Services/
│       │   ├── Plugins/            # PluginLoader, PluginManager, PluginEventBus, PluginRegistryService
│       │   └── Providers/          # LocalProviderBase
│       ├── ViewModels/             # MVVM view models
│       ├── Views/                  # MainWindow, SettingsWindow, WelcomeWindow, sections
│       ├── Resources/              # Icons, sounds, silero_vad.onnx
│       └── App.xaml.cs             # Composition root
├── plugins/                        # Plugin source (distributed via marketplace)
│   ├── TypeWhisper.Plugin.OpenAi/           # OpenAI transcription + LLM
│   ├── TypeWhisper.Plugin.Groq/             # Groq transcription + LLM
│   ├── TypeWhisper.Plugin.Gemini/           # Google Gemini LLM
│   ├── TypeWhisper.Plugin.OpenAiCompatible/ # OpenAI-compatible servers
│   ├── TypeWhisper.Plugin.SherpaOnnx/       # Local ASR (Parakeet, Canary)
│   ├── TypeWhisper.Plugin.AssemblyAi/       # AssemblyAI transcription
│   ├── TypeWhisper.Plugin.Deepgram/         # Deepgram transcription
│   ├── TypeWhisper.Plugin.ElevenLabs/       # ElevenLabs Scribe transcription
│   ├── TypeWhisper.Plugin.Linear/           # Linear issue creation
│   ├── TypeWhisper.Plugin.Obsidian/         # Obsidian note creation
│   ├── TypeWhisper.Plugin.Script/           # Custom script execution
│   ├── TypeWhisper.Plugin.LiveTranscript/   # Live transcription window
│   └── TypeWhisper.Plugin.Webhook/          # Webhook event notifications
└── tests/
    ├── TypeWhisper.Core.Tests/           # xUnit + Moq, in-memory SQLite
    └── TypeWhisper.PluginSystem.Tests/   # Plugin infrastructure tests
```

**Patterns:** MVVM with CommunityToolkit.Mvvm. `App.xaml.cs` is the composition root. SQLite for persistence with a custom migration pattern. Plugin system with AssemblyLoadContext isolation and manifest-based discovery. Post-processing pipeline with priority-based ordering.

**Key dependencies:** NAudio (audio), NHotkey.Wpf (hotkeys), WPF-UI (Fluent Design), Microsoft.ML.OnnxRuntime (local translation), Velopack (updates), H.NotifyIcon.Wpf (system tray).

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
