using TypeWhisper.Core.Services;

namespace TypeWhisper.Windows.Services;

public static class WatchFolderExportBuilder
{
    public static WatchFolderExportArtifact Build(
        WatchFolderOutputFormat format,
        WatchFolderTranscriptionResult result,
        string fileName,
        string engineName,
        DateTime date)
    {
        return format switch
        {
            WatchFolderOutputFormat.PlainText => new WatchFolderExportArtifact("txt", result.Text),
            WatchFolderOutputFormat.Srt => BuildSubtitle("srt", result),
            WatchFolderOutputFormat.Vtt => BuildSubtitle("vtt", result),
            _ => BuildMarkdown(result, fileName, engineName, date)
        };
    }

    private static WatchFolderExportArtifact BuildMarkdown(
        WatchFolderTranscriptionResult result,
        string fileName,
        string engineName,
        DateTime date)
    {
        var content = $"""
        # Transcription: {fileName}
        - Date: {date:G}
        - Engine: {engineName}

        {result.Text}
        """;

        return new WatchFolderExportArtifact("md", content);
    }

    private static WatchFolderExportArtifact BuildSubtitle(
        string extension,
        WatchFolderTranscriptionResult result)
    {
        if (result.Segments.Count == 0)
            throw new InvalidOperationException("Subtitle export requires timestamped transcription segments.");

        var content = extension == "srt"
            ? SubtitleExporter.ToSrt(result.Segments)
            : SubtitleExporter.ToWebVtt(result.Segments);

        return new WatchFolderExportArtifact(extension, content);
    }
}
