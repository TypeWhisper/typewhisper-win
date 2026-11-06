using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Voxtral part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) => PluginDictionaryTerms.ParsePrompt(prompt);
    // The model catalog was fetched with the previous key; a new key may see a different set of models.
    partial void OnKeyReplacing(ref Configuration next, string? key)
    {
        if (string.Equals(key, Key, StringComparison.Ordinal)) return;
        var values = new Dictionary<string, string>(next.Values);
        values.Remove("modelCatalog");
        next = next with { Values = values };
    }

    /// <summary>
    /// Uploads a recording to <c>/v1/audio/transcriptions</c>. Without a pinned language the request asks for the detected
    /// one: Transcribe 3 leaves <c>language</c> empty otherwise, and rejects <c>return_language</c> next to a pinned language.
    /// </summary>
    internal async Task<PluginTranscriptionResult> MistralTranscriptionAsync(string model, byte[] audio, string? language,
        IReadOnlyList<string> contextBias, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "https://api.mistral.ai/v1/audio/transcriptions");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new("audio/wav"); form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        if (language is not null) form.Add(new StringContent(language), "language");
        else form.Add(new StringContent("true"), "return_language");
        // Repeated fields keep multi-word names and phrases intact.
        foreach (var term in contextBias) form.Add(new StringContent(term), "context_bias");
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
        var detected = Text(root, "language") is { Length: > 0 } reported ? reported : language;
        return new(text.Trim(), detected, Number(root, "duration"), noSpeech) { Segments = segments };
    }
}
