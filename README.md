# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Speech-to-text for Windows. Transcribe audio using on-device AI models — private by default, with optional cloud providers for higher accuracy or speed. Your voice data stays on your PC unless you explicitly enable a cloud provider.

## Features

- **On-device transcription** — All processing happens locally using ONNX Runtime. Two AI engines: Parakeet TDT 0.6B (25+ languages, fast) and Canary 180M Flash (EN/DE/FR/ES with translation)
- **Cloud transcription (optional)** — Groq and OpenAI as alternative providers. API keys encrypted at rest via DPAPI
- **System-wide dictation** — Three independent hotkeys: Hybrid (short press toggles, long press is push-to-talk), Toggle-only, and Hold-only. Auto-pastes into any app
- **Non-blocking pipeline** — Multiple recordings can be queued while transcription runs in the background
- **Live partial results** — Silero VAD detects speech segments during recording and transcribes them in real time, showing results in the overlay before recording stops
- **Translation** — Cloud LLM translation (Groq: Llama 3.3 70B, OpenAI: GPT-4o-mini) with local Marian ONNX model fallback. 20 target languages: EN, DE, FR, ES, IT, NL, PL, SV, DA, FI, CS, RU, UK, HU, JA, ZH, AR, HI, VI, ID
- **File transcription** — Drag-and-drop audio/video files. Supports WAV, MP3, M4A, AAC, OGG, FLAC, WMA, MP4, MKV, AVI, MOV, WebM. Export as TXT, SRT, or WebVTT
- **App-specific profiles** — Per-app and per-website overrides for language, task, and whisper mode. Match by process name and/or URL pattern. Automatically activates when dictating in a matched application or website
- **Dictionary** — Custom term corrections applied after transcription (e.g., fix names, jargon, or recurring misrecognitions). Regex support. 13 built-in term packs: Web Dev, .NET/C#, DevOps, Data & AI, Design, Game Dev, Mobile, Security, Databases, Medical, Legal, Finance, Music Production
- **Snippets** — Text shortcuts with trigger→replacement. Placeholders: `{date}`, `{time}`, `{datetime}`, `{clipboard}`, `{day}`, `{year}`. Date/time support custom formats (e.g. `{date:dd.MM.yyyy}`)
- **HTTP API** — Local REST server for integration with external tools (status, models, transcription)
- **History** — Searchable transcription history with raw/final text tracking and app context
- **Dashboard** — Usage statistics (words, duration, records) with activity chart
- **Welcome wizard** — Guided 5-step onboarding: model selection, cloud providers, microphone test, hotkey setup
- **Windows autostart** — Optional start with Windows (via registry)
- **Sound feedback** — Audio cues for recording start and stop
- **Media pause** — Automatically pauses media playback during recording
- **Audio ducking** — Reduces system volume while recording
- **Whisper mode** — Boosted microphone gain for quiet speech
- **Silence detection** — Automatically stops recording after configurable silence period
- **Audio normalization** — Automatic gain control for consistent input levels
- **Auto-update** — Built-in updates via Velopack
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

Both models run on CPU with int8 quantization — no GPU required.

### Cloud Models (optional)

| Provider | Model | Notes |
|----------|-------|-------|
| Groq | whisper-large-v3 | Fast cloud transcription, supports translation |
| Groq | whisper-large-v3-turbo | Fastest, no translation |
| OpenAI | gpt-4o-transcribe | Highest accuracy |
| OpenAI | gpt-4o-mini-transcribe | Lower cost, good quality |
| OpenAI | whisper-1 | Classic, supports translation |

Cloud providers require an API key configured in Settings or during the welcome wizard.

## HTTP API

TypeWhisper can run a local HTTP server (default port 9876, configurable in Settings) for integration with external tools.

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/status` | GET | App status and active model |
| `/v1/models` | GET | List all available models (local + cloud) |
| `/v1/transcribe` | POST | Transcribe audio from request body. Query params: `language`, `task` (transcribe/translate) |

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

## Profiles

Profiles let you configure transcription settings per application or website. For example:

- **Outlook** — German language
- **Slack** — English language
- **Terminal** — Whisper mode always on
- **github.com** — English language (matches in any browser)
- **docs.google.com** — German language, translate to English

Create profiles in Settings > Profiles. Assign process names and/or URL patterns, set language/task overrides, and adjust priority. URL patterns support wildcard matching — e.g. `*.github.com` matches `gist.github.com`.

When you start dictating, TypeWhisper matches the active window and browser URL against your profiles with the following priority:
1. **Process + URL match** — highest specificity (e.g. chrome.exe + github.com)
2. **URL-only match** — cross-browser profiles (e.g. github.com in any browser)
3. **Process-only match** — generic app profiles (e.g. all of Chrome)

The active profile is shown in the recording overlay.

## Architecture

```
typewhisper-win/
├── src/
│   ├── TypeWhisper.Core/           # Core logic (net10.0)
│   │   ├── Data/                   # SQLite database with migrations
│   │   ├── Interfaces/             # Service contracts
│   │   ├── Models/                 # Profile, AppSettings, ModelInfo, TermPack, etc.
│   │   ├── Services/
│   │   │   └── Cloud/              # CloudProviderBase, GroqProvider, OpenAiProvider
│   │   └── Translation/            # MarianTokenizer, MarianConfig (local ONNX translation)
│   └── TypeWhisper.Windows/        # WPF UI layer (net10.0-windows)
│       ├── Controls/               # HotkeyRecorderControl
│       ├── Native/                 # P/Invoke, KeyboardHook
│       ├── Services/
│       │   └── Providers/          # LocalProviderBase, ParakeetProvider, CanaryProvider
│       ├── ViewModels/             # MVVM view models
│       ├── Views/                  # MainWindow, SettingsWindow, WelcomeWindow, sections
│       ├── Resources/              # Icons, sounds, silero_vad.onnx
│       └── App.xaml.cs             # Composition root
└── tests/
    └── TypeWhisper.Core.Tests/     # xUnit + Moq, in-memory SQLite
```

**Patterns:** MVVM with CommunityToolkit.Mvvm. `App.xaml.cs` is the composition root. SQLite for persistence with a custom migration pattern.

**Key dependencies:** NAudio (audio), NHotkey.Wpf (hotkeys), sherpa-onnx (local ASR), Microsoft.ML.OnnxRuntime (local translation), Velopack (updates), H.NotifyIcon.Wpf (system tray).

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
