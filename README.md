# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text and AI text processing for Windows. Transcribe audio using on-device AI models or cloud APIs (Groq, OpenAI, OpenAI-compatible servers), then process the result with custom LLM prompts. Your voice data stays on your PC with local models — or use cloud APIs for faster processing.

## Features

### Transcription

- **On-device models** — Parakeet TDT 0.6B (25+ languages, fast) and Canary 180M Flash (EN/DE/FR/ES with translation), running on CPU via SherpaOnnx with int8 quantization — no GPU required
- **Cloud transcription** — Groq Whisper, OpenAI Whisper, and any OpenAI-compatible server (Ollama, LM Studio, vLLM). API keys encrypted at rest via DPAPI
- **Streaming preview** — Silero VAD detects speech segments during recording and transcribes them in real time, showing partial results in the overlay before recording stops
- **File transcription** — Drag-and-drop audio/video files. Supports WAV, MP3, M4A, AAC, OGG, FLAC, WMA, MP4, MKV, AVI, MOV, WebM
- **Subtitle export** — Export transcriptions as TXT, SRT, or WebVTT

### Dictation

- **System-wide** — Three independent hotkeys: Hybrid (short press toggles, long press is push-to-talk), Toggle-only, and Hold-only. Auto-pastes into any app
- **Non-blocking pipeline** — Multiple recordings can be queued while transcription runs in the background
- **Sound feedback** — Audio cues for recording start and stop
- **Silence detection** — Automatically stops recording after configurable silence period
- **Whisper mode** — Boosted microphone gain for quiet speech
- **Audio normalization** — Automatic gain control for consistent input levels
- **Media pause** — Automatically pauses media playback during recording
- **Audio ducking** — Reduces system volume while recording

### AI Processing

- **Custom prompts** — Process transcriptions (or any text) with LLM prompts. Standalone Prompt Palette via global hotkey — a floating panel for AI text processing independent of dictation
- **LLM providers** — Groq, OpenAI, and any OpenAI-compatible server (Ollama, LM Studio, vLLM)
- **Translation** — Cloud LLM translation (Groq: Llama 3.3 70B, OpenAI: GPT-4o-mini) with local Marian ONNX model fallback. 20 target languages: EN, DE, FR, ES, IT, NL, PL, SV, DA, FI, CS, RU, UK, HU, JA, ZH, AR, HI, VI, ID

### Personalization

- **Profiles** — Per-app and per-website overrides for language, task, transcription model, and whisper mode. Match by process name and/or URL pattern with wildcard support. Automatically activates when dictating in a matched application or website
- **Dictionary** — Custom term corrections applied after transcription (e.g., fix names, jargon, or recurring misrecognitions). Regex support. 13 built-in term packs: Web Dev, .NET/C#, DevOps, Data & AI, Design, Game Dev, Mobile, Security, Databases, Medical, Legal, Finance, Music Production
- **Snippets** — Text shortcuts with trigger→replacement. Placeholders: `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, `{year}`. Date/time support custom formats (e.g. `{date:dd.MM.yyyy}`)
- **History** — Searchable transcription history with raw/final text tracking, app context, and inline editing

### Integration & Extensibility

- **Plugin system** — Extensible plugin architecture with SDK and marketplace. Create custom plugins for transcription engines, LLM providers, post-processors, or action plugins. Plugins for OpenAI, Groq, OpenAI Compatible, SherpaOnnx, and Webhook available via the built-in marketplace
- **Plugin marketplace** — Browse, install, update, and uninstall plugins directly from Settings
- **HTTP API** — Local REST server for integration with external tools (status, models, transcription)

### General

- **Fluent Design** — WPF-UI with Mica backdrop, native title bar, and Fluent controls
- **Dynamic Island overlay** — Configurable widgets with multi-monitor support
- **Home dashboard** — Usage statistics (words, duration, records) with activity chart
- **Welcome wizard** — Guided 5-step onboarding: model selection, cloud providers, microphone test, hotkey setup
- **Auto-update** — Built-in updates via Velopack
- **Windows autostart** — Optional start with Windows (via registry)
- **System tray** — Minimizes to tray with quick access

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
| OpenAI Compatible | Any model | Local LLM servers (Ollama, LM Studio, vLLM) |

Cloud providers are loaded as plugins and can be configured in Settings > Erweiterungen.

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

4. The app appears in the system tray — the welcome wizard guides you through model download and setup.

## HTTP API

TypeWhisper can run a local HTTP server (default port 9876, configurable in Settings) for integration with external tools.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status and active model |
| `/v1/models` | GET | List all available models (local + cloud) |
| `/v1/transcribe` | POST | Transcribe audio from request body. Query params: `language`, `task` (transcribe/translate) |

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

The built-in marketplace provides SherpaOnnx (local models), OpenAI, Groq, OpenAI Compatible (local LLM servers), and Webhook plugins.

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
│       ├── Controls/               # HotkeyRecorderControl
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
│   ├── TypeWhisper.Plugin.OpenAiCompatible/ # OpenAI-compatible servers (Ollama, LM Studio, vLLM)
│   ├── TypeWhisper.Plugin.SherpaOnnx/       # Local ASR (Parakeet, Canary)
│   └── TypeWhisper.Plugin.Webhook/          # Webhook event notifications
└── tests/
    ├── TypeWhisper.Core.Tests/           # xUnit + Moq, in-memory SQLite
    └── TypeWhisper.PluginSystem.Tests/   # Plugin infrastructure tests
```

**Patterns:** MVVM with CommunityToolkit.Mvvm. `App.xaml.cs` is the composition root. SQLite for persistence with a custom migration pattern. Plugin system with AssemblyLoadContext isolation and manifest-based discovery.

**Key dependencies:** NAudio (audio), NHotkey.Wpf (hotkeys), WPF-UI (Fluent Design), Microsoft.ML.OnnxRuntime (local translation), Velopack (updates), H.NotifyIcon.Wpf (system tray).

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
