using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Exports timestamped transcription segments to subtitle text formats.
/// </summary>
public static class SubtitleExporter
{
    /// <summary>
    /// Converts segments to SubRip text using comma-separated millisecond timestamps.
    /// </summary>
    public static string ToSrt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.AppendLine((i + 1).ToString());
            sb.Append(FormatSrtTime(seg.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatSrtTime(seg.End));
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts segments to WebVTT text using period-separated millisecond timestamps.
    /// </summary>
    public static string ToWebVtt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.Append(FormatVttTime(seg.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatVttTime(seg.End));
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatSrtTime(double seconds)
    {
        // SRT uses HH:mm:ss,mmm, while VTT below uses HH:mm:ss.mmm.
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}
