using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>API text and the segments associated with that output language.</summary>
public sealed record ProcessedApiTranscript(string Text, IReadOnlyList<TranscriptionSegment> Segments);

/// <summary>Applies API-request translation and corrections without running app workflows or snippets.</summary>
public static class LocalApiTextProcessing
{
    /// <summary>Translates timed segments in bounded batches, preserving provider timing and order.</summary>
    public static async Task<ProcessedApiTranscript> ProcessTranscriptAsync(string text,
        IReadOnlyList<TranscriptionSegment> segments, bool applyCorrections,
        Func<string, CancellationToken, Task<string>>? translate,
        Func<string, CancellationToken, Task<string>>? translateSegments,
        Func<string, string>? correct, CancellationToken ct)
    {
        // Preserve whole-transcript context, acoustic refinements and formatting in the main text.
        var finalText = await ProcessAsync(text, applyCorrections, translate, correct, ct);
        if (translate is null || segments.Count == 0)
            return new(finalText, segments);
        if (translateSegments is null) throw new InvalidOperationException("Segment translation is unavailable.");

        var result = new List<TranscriptionSegment>(segments.Count);
        for (var offset = 0; offset < segments.Count;)
        {
            ct.ThrowIfCancellationRequested();
            var count = 0;
            var characters = 0;
            while (offset + count < segments.Count && count < 32)
            {
                var length = segments[offset + count].Text.Length;
                if (count > 0 && characters + length > 8000) break;
                characters += length;
                count++;
            }
            var input = JsonSerializer.Serialize(Enumerable.Range(offset, count)
                .Select(id => new { id, text = segments[id].Text }));
            var response = await translateSegments(input, ct);
            ct.ThrowIfCancellationRequested();
            string[] translated;
            try
            {
                using var json = JsonDocument.Parse(response);
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != count)
                    throw new JsonException();
                translated = new string[count];
                var index = 0;
                foreach (var entry in json.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 2 ||
                        !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                        !id.TryGetInt32(out var actualId) || actualId != offset + index ||
                        !entry.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(value.GetString()))
                        throw new JsonException();
                    translated[index++] = value.GetString()!.Trim();
                }
            }
            catch (JsonException)
            {
                throw new LocalApiRequestException(502, "Translation did not preserve the subtitle segments. Retry with another LLM model.");
            }
            for (var index = 0; index < count; index++)
            {
                var final = await ProcessAsync(translated[index], applyCorrections, null, correct, ct);
                result.Add(segments[offset + index] with { Text = final });
            }
            offset += count;
        }
        ct.ThrowIfCancellationRequested();
        return new(finalText, result);
    }

    /// <summary>Translates before dictionary correction and rejects canceled or empty output.</summary>
    public static async Task<string> ProcessAsync(string text, bool applyCorrections,
        Func<string, CancellationToken, Task<string>>? translate, Func<string, string>? correct,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (translate is not null) text = await translate(text, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Translation returned no text.");
        if (applyCorrections && correct is not null) text = correct(text);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Corrections returned no text.");
        return text;
    }
}
