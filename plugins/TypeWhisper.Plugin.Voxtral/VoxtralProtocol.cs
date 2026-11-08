using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    // Mistral sets no fixed limit for context_bias; terms count against the model's context window.
    // 400 terms and 200 sentences passed in a live test (2026-11-06), so this leaves headroom.
    private static readonly DictionaryTermsBudget DictionaryBudget = new(MaxTerms: 200, MaxCharsPerTerm: 200, MaxTotalChars: 8000);

    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => SelectedTranscription?.Languages ?? [];
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => SelectedTranscription?.ContextBias == true;
    /// <inheritdoc />
    public bool SupportsStructuredDictionaryTerms => true;
    /// <inheritdoc />
    public DictionaryTermsBudget DictionaryTermsBudget => DictionaryBudget;

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        ProviderConnection.Audio(wavAudio, translate, false, ct);
        var selected = SelectedTranscription
            ?? throw new PluginRequestException("No transcription model is available. Refresh models.", PluginRequestFailureKind.Configuration);
        if (selected.Realtime)
        {
            // A complete recording uploads in about a second; streaming it through the live socket takes most of its length.
            if (BatchModel is { } batch) selected = batch;
            else return TranscribeRealtimeAsync(wavAudio, language, prompt, ct);
        }
        return TranscribeBatchAsync(selected, wavAudio, language, prompt, ct);
    }

    private async Task<PluginTranscriptionResult> TranscribeBatchAsync(MistralTranscriptionModel model, byte[] wavAudio, string? language,
        string? prompt, CancellationToken ct)
    {
        var result = await Connection.MistralTranscriptionAsync(model.Id, wavAudio, LanguageFor(model, language), ContextBias(model, prompt), ct)
            .ConfigureAwait(false);
        return result.DurationSeconds > 0 ? result : result with { DurationSeconds = WavDuration(wavAudio) };
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        using var request = Connection.Request(HttpMethod.Get, "https://api.mistral.ai/v1/models");
        using var response = await Connection.ReadAsync(request, ct); _ = ProviderConnection.Required(response.RootElement, "data", JsonValueKind.Array);
    }

    /// <summary>A language the model accepts, or null to detect it. Mistral rejects codes outside the model's list.</summary>
    private static string? LanguageFor(MistralTranscriptionModel model, string? language)
    {
        if (ProviderConnection.Language(language) is not { } value) return null;
        var code = value.Split('-', '_')[0].ToLowerInvariant();
        return model.Languages.Contains(code, StringComparer.Ordinal) ? code : null;
    }

    private static IReadOnlyList<string> ContextBias(MistralTranscriptionModel model, string? prompt) =>
        model.ContextBias ? PluginDictionaryTerms.Clip(PluginDictionaryTerms.ParsePrompt(prompt), DictionaryBudget) : [];

    private static double WavDuration(byte[] wav)
    {
        try { return ExtractPcm(wav).Length / 32000d; }
        catch (InvalidDataException) { return 0; }
    }
}
