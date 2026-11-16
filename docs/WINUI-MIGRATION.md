# WinUI application architecture

`src/TypeWhisper.WinUI` is the sole application host. It owns the UI, platform services, icon, provider logos, audio cues and license strings.

`TypeWhisper.Presentation` contains application logic independent of the desktop framework. `TypeWhisper.Core` supplies shared models, persistence and processing. `TypeWhisper.PluginHost` loads portable providers through the UI-independent SDK; provider settings are rendered by the WinUI host.

`ApplicationServices` constructs the profile's history, dictation session and local
API. `MainWindow` connects that graph to views and coordinates shutdown admission.
`DictationPluginServices` owns package discovery, worker configuration, local
transcription/CTC providers, the runtime registry, shared model-idle policy and
speech backend. Its shutdown attempts every provider release even when one fails.
The session drains active capture, speech and downloads before disposing that graph.

Settings use one keyed shortcut callback, and `ViewSubscriptions` pairs external
event subscriptions with view load/unload without duplicate attachment. The remaining
dictation session coordinates capture and output; it is still a large class and
should be split further along tested behavior boundaries when those paths change.

## History search index

`TypeWhisper.Presentation` keeps a full-text index of History (`HistorySearchIndex`,
`HistorySearchIndexer`) that optional modules read through `IHistorySearching` in
the ModuleKit. It is a SQLite file with FTS5 in the profile folder, at
`HistorySearch/index.sqlite`, from the `Microsoft.Data.Sqlite` package and its
bundled native SQLite.

- **Derived data.** Every row can be made again from History. A file that is damaged
  or has another format (`HistorySearchIndex.FormatVersion`) is deleted and built
  again; so is an index whose full-text tables fail the integrity check at startup.
- **Off until asked for.** The host builds the index only after a module calls
  `IHistorySearching.PrepareAsync`. Builds without modules never create it. Once the
  file exists, the indexer keeps it in step after each start until a module calls
  `DisableAsync`.
- **In step with History.** Each pass compares History with the content hashes the
  index holds. Removed records leave the index at once, added and changed ones are
  written two seconds after the last change in batches of 50 and wait while a
  dictation runs. Clearing History deletes the file and starts an empty one.
- **Privacy.** Deleted rows are overwritten (`secure_delete` and FTS5
  `secure-delete`), and reads return only records whose indexed content matches
  History, so deleted or edited text is never returned while the indexer catches up.
- **User data.** "Export all data" leaves the `HistorySearch` folder out
  (`UserDataExport.IsExcluded`). "Delete all data" removes it with the rest of the
  profile; the indexer stops and closes the file before a restore or a deletion.

## Build and validation

Use the [current build instructions](../README.md#build) and [test guide](../TESTING_GUIDE.md); the maintainer's helper script is described in [Maintainer's local setup](../TESTING_GUIDE.md#maintainers-local-setup).

`CI` builds the WinUI solution and runs `eng/Test-WinUIHeadless.ps1 -Suite App` for application and plugin host/SDK tests on Windows and Linux. Windows also runs the platform-service tests without a desktop UI framework. `Plugins` runs the plugin-owned tests on both systems with `-Suite Plugins`. The script defaults to all suites for local checks and `Candidate`. `Packaging`, `Candidate` and `Store` all target the WinUI application.

For supported profile import and package compatibility, see [Daily delivery and migration](DAILY-1.1-CANDIDATE.md) and the [plugin package contract](PLUGIN-PACKAGES-1.1.md).
