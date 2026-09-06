# WinUI integration architecture

For current completion status and release priorities, use the [full Windows and Mac comparison](WINUI-FUNCTIONAL-STATUS.md) and [progress summary](WINUI-PROGRESS.md). This document describes the present integration boundaries rather than the chronology of earlier slices.

## Product decisions

Version 1.1 is a greenfield application. Reuse proven provider/plugin source while changing SDK, settings and packaging contracts where useful. Plugins will be rebuilt and republished; old WPF plugin binaries are not a compatibility requirement. Existing WPF targets are development/reference aids, not constraints on the new architecture.

Integrations has Installed and Discover tabs. NVIDIA Parakeet and Groq are the two currently connected integrations. CTC is NVIDIA's internal dependency and automatically follows its enablement. It has no separate integration or toggle, and Parakeet has no experimental product label. This does not establish acoustic parity with Mac/FluidAudio.

Dictation selects a provider first and then a ready model. Plugin settings own model download/removal and API-key entry. History exposes actual provider/model in details only. The redundant global Models settings page was removed.

The sole 1.1 catalog is `https://typewhisper.github.io/typewhisper-win/plugins-v2.json`. There is no community feed or fallback to the old registry. The planned URL is fixed; publication and real remote acceptance remain outstanding. See [package contract](PLUGIN-PACKAGES-1.1.md).

The approved prototype is the UI foundation. Retain its useful interaction patterns while replacing examples with real services. Do not treat an existing screen or settings control as completed integration. The old WPF application remains the shipping host. Old personal history import is not required; no existing production data is automatically moved, rewritten or deleted.

## Host and storage

Run local application builds/launches only through:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run --winui <checkout>
```

Output is `F:/typewhisper/dev-output/typewhisper-win/WinUI`. The host has no compile-time dependency on experiments. WinUI publish includes its XBF/PRI resources; package builds discover `plugins/*/portable.proj` rather than referencing individual provider projects.

The current project targets `net10.0-windows10.0.26100.0`, minimum build 26100, with x64/ARM64 identifiers. This is not a decision to drop older Windows support or proof of ARM64 release acceptance. Installer/update/signing and architecture validation must be completed before changing the shipping host.

WinUI state lives under `LocalAppData/TypeWhisper-WinUI-DevUserData`: history, dictionary/snippets, audio preferences, hotkeys, overlay, plugin settings/secrets and installed-package index. Local model assets can use the older isolated `TypeWhisper-DevUserData/PluginData` development directory. Production migration is disabled. Persisted slices coexist with a session-only settings dictionary; a coherent settings boundary is still missing.

## Presentation and history

`TypeWhisper.Presentation` targets plain `net10.0` and references Core. `HistoryReader` receives its history service and returns read-only snapshots with explicit load/search failures and cancellation. It has no default user-data path, persistence writes or WPF dispatcher/dialog dependency.

The adopted history UI uses the isolated history service without sample seeding. Corrupt data propagates to retryable error state without becoming an empty writable file. Display IDs are derived without rewriting opaque stored IDs. Missing kind/device metadata stays unknown. Audio projection, edit/delete/export, retention, Inbox and recovery are not connected.

The dictation writer currently saves raw/final text, duration, engine/model and task `transcribe`; it does not populate language/app/URL or audio metadata. History saves before insertion. A failed save preserves the result only in memory and prevents paste; this is not durable recovery.

## Dictation and insertion

`LocalDictationSession` coordinates one operation at a time. Main Dictation captures immediately on key-down; the 300-ms threshold decides whether release finishes capture or leaves toggle recording active. Extra chord keys cancel speculative capture. Audio preferences are applied at the next recording. Provider/model configuration uses its own phase so it does not reopen the recording overlay.

The existing portable NVIDIA source supplies PCM transcription, Parakeet/Canary metadata, downloads and token timings. WinUI currently uses CPU. Local preview is cancelled and drained before final decoding shares the recognizer. Groq receives final recorded audio only; there is no cloud live preview. Groq settings use per-user encrypted secrets; its LLM capability is not yet consumed by the host.

Final processing currently runs CTC when eligible, then dictionary/text boosting, then snippet expansion. Recording snapshots prevent stale work from overwriting newer editor changes. This is not the complete legacy priority pipeline; normalization, spoken formatting, regional variants, translation and arbitrary plugin processors remain unconnected. Snippet/dictionary ordering differs from the legacy pipelines and needs a deliberate integration decision.

The inserter sends one Ctrl+V for the complete text, preserving supported clipboard data through the native transaction. Unsupported/unmaterializable snapshots abort before replacement. Restore checks sequence ownership and leaves newer clipboard data alone. Temporary text is marked for clipboard-history exclusion. No automatic Enter or paste retry is sent.

The target guard follows the top-level foreground window, not the exact editor field. Ctrl+V dispatch is not evidence that an editor consumed the text; restoration currently waits 500 ms. Review-first and exact-field settings are not runtime controls yet. Real rich-format clipboard and application acceptance remain necessary.

## Packages and capabilities

Portable inventory inspects metadata without activating entry points. The installed-package index is initialized from development bundles once. Safe extraction, size/hash and compatibility checks precede registration. Updates are staged for restart. Uninstallation disables/drains the supported runtime, runs its optional callback, removes registration and retains keys/models/preferences; unused binaries are collected on startup.

`IPluginInstallationLifecycle` adds optional `OnInstallAsync` and `OnUninstallAsync`. The context supplies host services, previous version and optional progress. Messages/fractions describe installation/deinstallation, not history text modifications. Runtime deactivation/disposal remains distinct from installation hooks.

Package storage/build discovery is generic, but runtime/settings composition still explicitly handles NVIDIA and Groq and the internal CTC dependency. A package can be installed without its capability being integrated. Define generic capabilities/settings/lifetimes before adding many more plugins. Existing SHA-256 verification is not publisher authentication or sandboxing; plugins execute in process.

`IPluginUserInterfaceProvider` is a prepared portable command-descriptor contract inspired by Mac. Tray and Quick Launch adapters are not wired. They will need scoped IDs, enabled/lifetime checks, refresh on capability changes, unload removal, duplicate prevention, attribution, async error/cancellation handling and icon fallback. Keep action text processing separate from UI-command invocation.

## Application lifetime and remaining services

The app owns single-instance redirection, launcher activation, tray navigation/status/finish and exit. Closing hides the launcher. Quick Launch alternatives are persisted; registration conflicts preserve old bindings. Overlay preferences are persisted and applied to the real overlay. Shutdown currently uses best-effort asynchronous disposal; busy inference and package operations require release acceptance.

Recorder, file queue, workflows, usage statistics, onboarding, account, backup and sync still contain demo behavior. Autostart, localization, HTTP/CLI, shell activation and the production update/licensing composition are missing. Linking existing audio platform classes into the project does not make the recorder functional.

Prefer portable services for decisions, jobs and state transitions, with narrow Windows adapters for capture, hotkeys, clipboard, windows and dialogs. The next integrated slice should include persistence and failed-operation behavior, not just a rendered control. Use [headless checks and separate native acceptance](WINUI-TESTING.md).
