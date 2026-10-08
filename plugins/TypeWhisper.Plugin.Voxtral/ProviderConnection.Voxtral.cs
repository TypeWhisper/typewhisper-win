using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Voxtral part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) =>
        prompt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    // The model catalog was fetched with the previous key; a new key may see a different set of models.
    partial void OnKeyReplacing(ref Configuration next, string? key)
    {
        if (string.Equals(key, Key, StringComparison.Ordinal)) return;
        var values = new Dictionary<string, string>(next.Values);
        values.Remove("modelCatalog");
        next = next with { Values = values };
    }
    internal async Task<PluginTranscriptionResult> MultipartAsync(string url, string model, byte[] audio, string? language,
        string? prompt, CancellationToken ct, string? key = null, string fileField = "file", string? responseFormat = null)
    {
        using var request = Request(HttpMethod.Post, url, key);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new("audio/wav"); form.Add(file, fileField, "audio.wav");
        form.Add(new StringContent(model), "model");
        if (responseFormat is not null) form.Add(new StringContent(responseFormat), "response_format");
        if (Language(language) is { } lang) form.Add(new StringContent(lang), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        request.Content = form;
        using var document = await ReadAsync(request, ct).ConfigureAwait(false); var root = document.RootElement;
        var text = Text(root, "text") ?? throw InvalidResponse();
        var segments = new List<PluginTranscriptionSegment>(); float? noSpeech = null;
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var start = Number(segment, "start"); var end = Number(segment, "end");
                if (end >= start && Text(segment, "text") is { } segmentText) segments.Add(new(segmentText, start, end));
                var probability = Number(segment, "no_speech_prob", -1);
                if (probability is >= 0 and <= 1) noSpeech = noSpeech is null ? (float)probability : Math.Min(noSpeech.Value, (float)probability);
            }
        }
        return new(text.Trim(), Text(root, "language") ?? Language(language), Number(root, "duration"), noSpeech) { Segments = segments };
    }
}
