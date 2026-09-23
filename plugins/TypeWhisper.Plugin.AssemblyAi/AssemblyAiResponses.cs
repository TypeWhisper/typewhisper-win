using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed partial class AssemblyAiPlugin
{
    internal static PluginTranscriptionResult ParseCompleted(JsonElement root, string? fallbackLanguage, bool diarization)
    {
        if (!root.TryGetProperty("text", out var textField) || textField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw InvalidResponse();
        var text = textField.GetString() ?? ""; // A completed silent recording legitimately returns null.
        var segments = ReadSegments(root, diarization ? "utterances" : "words", diarization);
        if (diarization && segments.Count > 0) text = string.Join("\n", segments.Select(s => s.Text));
        if (diarization && segments.Count == 0) segments = ReadSegments(root, "words", labelSpeakers: false);
        var duration = Number(root, "audio_duration") ?? 0;
        if (segments.Count > 0) duration = Math.Max(duration, segments.Max(s => s.End));
        return new(text, OptionalString(root, "language_code") ?? fallbackLanguage, duration, NoSpeechProbability: null) { Segments = segments };
    }

    private static List<PluginTranscriptionSegment> ReadSegments(JsonElement root, string name, bool labelSpeakers)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return segments;
        foreach (var item in values.EnumerateArray())
        {
            var text = OptionalString(item, "text")?.Trim();
            if (string.IsNullOrEmpty(text) || Number(item, "start") is not { } start || Number(item, "end") is not { } end || end < start) continue;
            if (labelSpeakers && Speaker(item) is { } speaker) text = speaker + ": " + text;
            segments.Add(new(text, start / 1000, end / 1000));
        }
        return segments;
    }

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number
        && field.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0 ? value : null;

    private static string? Speaker(JsonElement item)
    {
        if (!item.TryGetProperty("speaker", out var field)) return null;
        var value = field.ValueKind switch
        {
            JsonValueKind.String => field.GetString()?.Trim(),
            JsonValueKind.Number when field.TryGetInt64(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return value.StartsWith("Speaker ", StringComparison.OrdinalIgnoreCase) ? value : "Speaker " + value;
    }
}
