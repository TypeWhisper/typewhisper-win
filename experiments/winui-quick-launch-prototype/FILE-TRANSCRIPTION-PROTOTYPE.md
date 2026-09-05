# File transcription preview

Question: does an import queue with per-file progress and a separate result/export surface remain clear inside the compact launcher?

One direction using the accepted Inter, blue-icon and dark-surface system. Import and results scroll; status and breadcrumb/actions stay at the bottom. Enter through Quick Launch → Transcribe file, or launch the isolated executable with `--files`.

New files: `PrototypeFileQueue.cs`, `PrototypeFileTranscriptionView.cs`, and the standalone checks in `../winui-files-prototype-check/` (`Program.cs`, `TypeWhisper.FilesPrototypeCheck.csproj`). MainWindow and App contain only route/lifecycle wiring for this surface.

## Boundaries

The native multi-file picker and drag/drop retain paths only. No media is opened, decoded, uploaded, copied or deleted. Supported extensions are a preview filter, not proof that a codec is supported. The bounded queue supports 20 paths, case-insensitive duplicate rejection, sequential simulated progress, cancellation and retry. Leaving the view cancels unfinished jobs but preserves completed results in the session.

Try sample adds two fictional files. Try an error example adds an explicitly failing fixture; retrying that fixture fails again deliberately. Results are sample English text, not transcriptions or translations. Engine/language controls, real processing and History integration are deferred.

Export uses the native save dialog and writes an explicitly labeled sample TXT, SRT or WebVTT only after selection. Subtitle timing is fictional. Existing files are never overwritten (CreateNew); choose a new filename. No actual export is performed automatically by tests.

## Validation

`dotnet run --project experiments/winui-files-prototype-check/TypeWhisper.FilesPrototypeCheck.csproj`: 23 checks for types, duplicate handling, limits, progress, cancellation, retry, failed jobs, unfinished export rejection and text/subtitle formatting.

The initial view was visually inspected at 780×520. Computer Use inputs did not reliably change the rendered state, so native picker/drop, full interactive result flow and save-dialog behavior need manual verification. Original files and user folders were not used as test fixtures. High-DPI and narrow-window interaction need a separate pass.

Keep this isolated until Marco approves production integration.
