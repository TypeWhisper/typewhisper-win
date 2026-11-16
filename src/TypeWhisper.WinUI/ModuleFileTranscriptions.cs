using TypeWhisper.ModuleKit;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

// What file transcription hooks receive for an accepted result, and how they are called.
internal static class ModuleFileTranscriptions
{
    internal static ModuleFileTranscription Map(FileTranscriptionOutput result, string sourcePath, string? recordId) => new(
        recordId,
        sourcePath,
        result.Text,
        string.IsNullOrWhiteSpace(result.Language) ? null : result.Language,
        result.Duration,
        result.Segments.Select(segment => new ModuleTimedText(segment.Text, segment.Start, segment.End)).ToArray());

    // Every hook runs, also after one of them failed.
    internal static void Notify(IReadOnlyList<ModuleFileTranscriptionHook> hooks, ModuleFileTranscription transcription, Action<string, Exception> report)
    {
        foreach (var hook in hooks)
            ModuleCalls.Run("file-transcription-hook", hook.Id, () => hook.Completed(transcription), report);
    }
}
