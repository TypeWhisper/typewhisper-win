# File Memory

## Status: deferred, not approved for publication

Deferred after manual acceptance on 2026-09-22. Basic entry creation, saving, editing and search worked, but the profile-based interface was rejected and the feature's practical purpose was unclear. Preserve this branch as a prototype; do not publish it as a completed memory integration.

Before resuming:

- Define an explicit, user-controlled flow for saving useful information from dictation or text processing.
- Connect relevant stored information to later text processing, with clear controls for when it is used.
- Design an entry-management interface around memories: search with the list, a dedicated entry editor, and meaningful save/delete labels.
- Validate the complete remember-and-reuse workflow, rather than only storage CRUD.

The standalone entry editor is insufficient for release. Further cosmetic work is deferred with the integration.

File Memory stores entries in a local JSON file. Add and edit entries in the settings sidebar, search saved memories, or attach the **Remember in File Memory** action to a workflow. No account or API key is required.

Version `1.3.0`; plugin ID `com.typewhisper.file-memory`; minimum host contract `1.1.2`.

## Settings

- Select a saved memory in the sidebar to edit it.
- Use **+** to open a new draft. Nothing is stored until the shared save button is used.
- Save commits the complete entry atomically. Empty, oversized and duplicate entries are rejected.
- Search uses the current query without saving an edited entry. It shows up to ten results with a bounded text preview.
- Remove deletes only the selected entry after the host confirmation. Unsaved drafts can also be discarded.
- A document icon identifies the plugin in host navigation.

Existing entries without IDs retain their content. Stable IDs are assigned when loaded and persisted on the next successful write. Invalid files are exposed read-only; the plugin preserves the existing file rather than replacing it with an empty list. Failed or cancelled writes preserve the previous in-memory and on-disk state.

## Platform scope

The macOS File Memory implementation was compared with this port. This Windows package provides a usable local entry editor, storage/search interface and an explicit workflow action. It does **not** automatically extract facts from dictation or inject remembered context into LLM prompts. The shared Windows host does not yet provide that memory pipeline. Search currently matches text case-insensitively rather than using semantic embeddings.

Plugin data stays in its host-owned data directory. No legacy or macOS files are imported automatically.

## Validation

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.FileMemory/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.FileMemory/Tests -c Release
```

23 tests pass, covering draft/save/edit/restart behavior, stable entry identity, search without saving drafts, duplicate and invalid content rejection, stale selection protection, selected deletion, cancelled/failed writes, concurrent storage, legacy entry loading, malformed-file protection, workflow execution and isolated package install/enable/restart/uninstall/reinstall.

The package is staged at `bin/Release/portable-host/Plugins/com.typewhisper.file-memory` inside this project. Package that directory as the ZIP root. Version 1.3.0 was installed over 1.2.0 in the development profile; all unrelated package receipts were preserved. The WinUI application was built and started through the canonical development helper. Manual storage operations were confirmed working, but the interface and overall product integration were not accepted. Publication is deferred.
