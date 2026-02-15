# TypeWhisper for Windows

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Windows](https://img.shields.io/badge/Windows-10%2B-0078D4.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com)

Local speech-to-text for Windows. Transcribe audio using on-device AI models — no cloud, no API keys, no subscriptions. Your voice data never leaves your PC.

## Features

- **On-device transcription** — All processing happens locally using ONNX Runtime
- **Two AI engines** — Parakeet TDT 0.6B (25+ languages, fast) and Canary 180M Flash (EN/DE/FR/ES with translation)
- **System-wide dictation** — Hybrid recording mode: short press toggles, long press is push-to-talk. Auto-pastes into any app
- **Translation** — On-device translation between English, German, French, and Spanish (Canary)
- **File transcription** — Process audio files with batch support
- **Subtitle export** — Export transcriptions as SRT with timestamps
- **App-specific profiles** — Per-app and per-website overrides for language, task, and whisper mode. Match by process name and/or URL pattern. Automatically activates when dictating in a matched application or website
- **Dictionary** — Custom term corrections applied after transcription (e.g., fix names, jargon, or recurring misrecognitions). Includes importable term packs with regex support
- **Snippets** — Text shortcuts with trigger→replacement. Supports placeholders like `{{DATE}}`, `{{TIME}}`, and `{{CLIPBOARD}}`
- **History** — Searchable transcription history with raw/final text tracking and app context
- **Dashboard** — Usage statistics (words, duration, records) with activity chart
- **Sound feedback** — Audio cues for recording start and stop
- **Media pause** — Automatically pauses media playback during recording
- **Audio ducking** — Reduces system volume while recording
- **Whisper mode** — Boosted microphone gain for quiet speech
- **Silence detection** — Automatically stops recording after configurable silence period
- **Audio normalization** — Automatic gain control for consistent input levels
- **Auto-update** — Built-in updates via Velopack
- **System tray** — Minimizes to tray with quick access

## System Requirements

- Windows 10 or later (x64)
- 8 GB RAM minimum, 16 GB+ recommended for larger models
- ~700 MB disk space for the Parakeet model, ~200 MB for Canary

## Model Recommendations

| Use Case | Recommended Model |
|----------|------------------|
| General transcription (25+ languages) | Parakeet TDT 0.6B (670 MB, int8) |
| Multilingual with translation (EN/DE/FR/ES) | Canary 180M Flash (198 MB, int8) |

Both models run on CPU with int8 quantization — no GPU required.

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

4. The app appears in the system tray — open Settings to download a model.

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
│   │   ├── Models/                 # Profile, AppSettings, ModelInfo, etc.
│   │   ├── Services/               # ProfileService, HistoryService, DictionaryService, SnippetService
│   │   └── Translation/            # Tokenizer and config
│   └── TypeWhisper.Windows/        # WPF UI layer (net10.0-windows)
│       ├── Controls/               # HotkeyRecorderControl
│       ├── Native/                 # P/Invoke, KeyboardHook
│       ├── Services/               # AudioRecording, ModelManager, HotkeyService, etc.
│       ├── ViewModels/             # MVVM view models
│       ├── Views/                  # MainWindow, SettingsWindow, sections
│       ├── Resources/              # Icons, sounds, silero_vad.onnx
│       └── App.xaml.cs             # Composition root
└── tests/
    └── TypeWhisper.Core.Tests/     # xUnit + Moq, in-memory SQLite
```

**Patterns:** MVVM with CommunityToolkit.Mvvm. No DI container — `App.xaml.cs` is the composition root. SQLite for persistence with a custom migration pattern.

**Key dependencies:** NAudio (audio), NHotkey.Wpf (hotkeys), sherpa-onnx (ASR engine), Velopack (updates), H.NotifyIcon.Wpf (system tray).

## License

GPLv3 — see [LICENSE](LICENSE) for details. Commercial licensing available — see [LICENSE-COMMERCIAL.md](LICENSE-COMMERCIAL.md). Trademark policy — see [TRADEMARK.md](TRADEMARK.md).
