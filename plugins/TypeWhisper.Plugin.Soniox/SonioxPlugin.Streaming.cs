using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Soniox;

public sealed partial class SonioxPlugin
{
    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(language is null ? [] : [language], prompt: null, ct);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, prompt: null, ct);

    /// <summary>Opens a live session that sends dictionary terms and the transcription context in Soniox's context.</summary>
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = _apiKey;
        if (string.IsNullOrEmpty(key))
            throw new PluginRequestException("Soniox API key required.", PluginRequestFailureKind.Configuration);
        var languages = languageHints.Select(NormalizeLanguage).Where(x => x is not null)
            .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var configuration = new SonioxStreamingConfiguration(
            EffectiveRealtimeModelId, languages, CreateRequestContext(prompt, _transcriptionContext));
        RefreshCatalogInBackgroundIfStale();
        return SonioxStreamingSession.ConnectAsync(key, _region, configuration, ct);
    }
}
