# Windows 1.1 capability map

This describes the current source, not a claim that every combination of device,
provider and package has passed native acceptance. Detailed historical work is in
the [functional journal](archive/winui/WINUI-FUNCTIONAL-STATUS.md).

| Area | Current behavior | Important boundary |
| --- | --- | --- |
| Dictation | Global shortcuts, configured recording modes, text processing, optional History and automatic insertion. | Target restoration and clipboard delivery can fail; the result explains the manual next step. |
| Result handling | Explicit insertion/processing/action reasons, copy feedback and optional plugin actions. | A storage warning does not prevent successful text delivery. See [dictation results](DICTATION-RESULTS.md). |
| Quick Launch | Search, pins, usage-ranked suggestions and workspace navigation. | The search changes scope in History and Workflows. This is not a global transcript search. |
| Recorder | Microphone/system capture and saved recording management. | Meeting automation and calendar sign-in are not available. |
| Files | Queue, result actions, exports and watched folders. | SRT/VTT require actual provider timings; failed exports and interrupted jobs have distinct recovery paths. |
| Workflows | Templates, app/site matching, dedicated shortcuts, explicit LLM selection/defaults and plugin destinations. | A missing provider is reported; it is not silently replaced with another service. |
| Dictionary and snippets | Terms, corrections, packs and spoken text expansion. | Provider-level vocabulary support and local text replacement are different mechanisms. |
| History | Search, copy/export, retention and optional retained audio. | History-off output can remain available in memory for the running session; that is not durable storage. |
| Plugins | Portable package installation, settings, models, updates and explicit capabilities. | WPF assemblies do not load. A package capability still needs a host consumer. |
| Memory | Explicit workflow memory sources and action-based storage. | Context is opt-in per workflow; see [File Memory acceptance](FILE-MEMORY-ACCEPTANCE.md). |
| Premium and sync | License/account access, correction learning and configured cloud-folder synchronization. | Local tests do not establish complete cross-platform or cloud-provider acceptance. |
| API and CLI | Local authenticated automation, transcription, model control, History, workflows and backups. | Follow the [API](WINUI-HTTP-API.md) and [CLI](WINDOWS-CLI.md) references. |
| Updates and migration | Architecture/channel-specific packages and copy-based 1.0 profile import. | Installed upgrade acceptance gates the original Daily rollout. Stable is separate. |

## Known release boundaries

- The first-dictation insertion fix for [#513](https://github.com/TypeWhisper/typewhisper-win/issues/513)
  (field capture retry for cold Chromium/Electron accessibility trees) still needs native confirmation after a fresh start.
- Package builds do not replace real ARM64 execution or older-Windows acceptance.
- Calendar adapters and research prototypes do not establish available calendar sign-in or meeting automation.
- Plugins published after an application Daily can have different source and validation revisions.
- Local translation supporting code is not a promise of an automatic Marian fallback in the WinUI workflow path.

See [release readiness](WINUI-PROGRESS.md), [platform scope](PLATFORM_PARITY.md),
[Premium/cloud evidence](windows-premium-cloud-parity.md) and the
[test guide](../TESTING_GUIDE.md) for the appropriate checks.
