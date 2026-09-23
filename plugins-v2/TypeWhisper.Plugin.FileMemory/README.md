# File Memory

File Memory keeps explicitly saved facts in a local JSON file. Version `1.4.0`, plugin ID `com.typewhisper.file-memory`, minimum host `1.1.5`.

## Remember and reuse

1. Add a fact in File Memory settings and choose **Save memory**, or select **Remember in File Memory** as a workflow’s **Action Target**. A Dictation Only workflow saves the transcript without LLM processing.
2. In a text-processing workflow, select **File Memory** under **Memory context**. This is off by default. Up to five matching entries are supplied to that workflow’s selected LLM provider as reference data.
3. Run that workflow on related text. For example, store “Project Aurora uses British English”, then ask about Aurora’s language convention.

No automatic fact extraction or global recall occurs. Dictation-only workflows do not read memory context. Storage is local; opted-in text workflows may send matching entries to their selected provider. Lexical retrieval ranks matching words and phrases; it is not semantic embedding search, and paraphrases without shared words may not match. No-match input proceeds unchanged. An unavailable configured memory source fails the workflow for review instead of silently substituting another source.

## Manage entries

The settings page has search alongside the entry list, a multiline editor and a single **Save memory** button. Search filters the complete saved text without writing drafts. Entries can be edited and removed individually. Existing entries are preserved during upgrades.

Writes are atomic; failed or cancelled saves retain the previous state. Corrupt files are exposed read-only rather than replaced. Duplicate and empty entries are rejected. The shared host binds memory queries to the selected plugin activation and cancels reads on disable/reload.

## Validation

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.FileMemory/Tests -c Release
dotnet test tests/TypeWhisper.Presentation.Tests -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests -c Release
```

Coverage includes CRUD, persistence, corruption protection, cancellation, ranked recall, package lifecycle, workflow action-to-memory retrieval through the real host, stale activation rejection, opt-in/no-op behavior, bounded prompt data and workflow editor round trips. Live UI and model acceptance are recorded separately; automated tests use no credentials.
