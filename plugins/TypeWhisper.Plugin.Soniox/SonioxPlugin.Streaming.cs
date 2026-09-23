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
        StartStreamingWithLanguageHintsAsync(language is null ? [] : [language], ct);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = _apiKey;
        if (string.IsNullOrEmpty(key))
            throw new PluginRequestException("Soniox API key required.", PluginRequestFailureKind.Configuration);
        var languages = languageHints.Select(NormalizeLanguage).Where(x => x is not null)
            .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return SonioxStreamingSession.ConnectAsync(key, _region, languages, ct);
    }
}
