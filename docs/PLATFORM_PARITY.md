# Platform parity and exclusions

TypeWhisper for Windows follows the same product goals as TypeWhisper for macOS: fast dictation, reusable workflows, transcription history, vocabulary support, local and cloud engines, and automation. Platform parity is measured by the user outcome, not by using the same operating-system framework or reproducing every macOS surface.

## Status definitions

| Status | Meaning |
|--------|---------|
| Supported | The capability is available on Windows. Its UI or implementation may differ from macOS. |
| Windows-native replacement | Windows provides the same product outcome through a different surface or technology. |
| Platform-specific non-goal | The capability depends on an Apple-only framework or product and is not a Windows parity target. |

## Supported parity targets

| Product capability | Windows status | Windows implementation |
|--------------------|----------------|------------------------|
| System-wide dictation | Supported | Global keyboard and mouse hotkeys, hold and toggle modes, background transcription, and automatic paste into the active application. |
| Application-aware behavior | Supported | Workflows can match applications, browser sites, and dedicated hotkeys, then select language, task, model, prompt processing, output, and actions. |
| File transcription | Supported | Local and cloud engines can transcribe common audio and video formats and export text, SRT, or WebVTT. |
| Local speech recognition | Supported | Local plugins include sherpa-onnx models such as Parakeet and Canary, whisper.cpp models, Granite Speech, and Cohere Transcribe. |
| Cloud speech recognition | Supported | Cloud engines are supplied through the Windows plugin system and marketplace. |
| AI text processing | Supported | Workflows use configured cloud LLM plugins or local models such as Gemma. This does not depend on Apple Intelligence. |
| Translation | Supported | Workflows can use configured LLM providers and local Marian ONNX fallback. Some transcription providers also expose audio-to-English translation. |
| Dictionary, snippets, and history | Supported | Windows-native settings and dashboard surfaces manage terms, corrections, snippets, and searchable transcription history. |
| Backup and restore | Supported | A versioned portable JSON backup covers workflows, user dictionary data, snippets, hotkeys, registry plugin references, text-only history, and an explicit allowlist of portable preferences. Credentials, licenses, audio, hardware selections, and machine-local paths are excluded. The same safe-merge implementation is available in Settings, the authenticated HTTP API, and the CLI. |
| Automation | Windows-native replacement | The local HTTP API and `typewhisper` CLI expose status, models, transcription, workflows, history, dictionary management, and dictation control. |
| Compact status surfaces | Windows-native replacement | The WPF recording overlay provides LED, timer, waveform, workflow, transcript preview, and microphone-level widgets. The app also provides a dashboard and system-tray controls. |
| Integrations | Windows-native replacement | The plugin marketplace, action plugins, HTTP API, and CLI replace launcher-specific integration contracts. |
| Extensibility | Supported | The .NET plugin SDK supports transcription engines, LLM providers, post-processors, memory providers, TTS providers, event observers, and actions. |

## Apple-only exclusions

| macOS technology or product | Windows parity status | Windows approach |
|-----------------------------|-----------------------|------------------|
| Apple Translate | Platform-specific non-goal | Translation is provided through TypeWhisper workflows, configured LLM providers, provider-native transcription translation, and the local Marian ONNX fallback. |
| Apple Intelligence | Platform-specific non-goal | AI processing uses explicit local or cloud provider plugins. TypeWhisper does not emulate or depend on Apple Intelligence services. |
| SpeechAnalyzer | Platform-specific non-goal | SpeechAnalyzer is an Apple speech framework. Windows uses its own local and cloud transcription plugins. |
| WhisperKit | Platform-specific non-goal | WhisperKit targets Apple platforms and Apple-native inference stacks. Windows offers whisper.cpp and other Windows-compatible local engines instead. |
| WidgetKit | Platform-specific non-goal | WidgetKit extensions are not portable to Windows. The WPF overlay, dashboard, and system tray are the supported TypeWhisper surfaces. A Windows Widgets integration is not required for parity. |
| Raycast | Platform-specific non-goal | A Raycast-specific extension is not a Windows deliverable. The HTTP API and CLI are the supported automation contracts for launchers, scripts, and external clients. |

## Scope rules

- A shared user outcome is a parity target when Windows can provide it with a reliable Windows-native implementation.
- Apple framework names, extension formats, and operating-system services are not themselves parity targets.
- A Windows replacement does not need identical settings or UI, but it should preserve the relevant workflow and document material behavioral differences.
- New cross-platform proposals should identify the user outcome first, then evaluate the appropriate implementation separately for each operating system.

For the current Windows feature set and automation contracts, see the main [README](../README.md).
