using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Speechmatics;

public sealed partial class SpeechmaticsPlugin
{
    /// <inheritdoc />
    public bool SupportsStreaming => true;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;

    internal Func<Uri, string, string, object, CancellationToken, Task<IStreamingSession>> ConnectStreaming { get; set; } = SpeechmaticsStreamingSession.ConnectAsync;

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(language is null ? [] : [language], null, ct);

    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languages, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languages, null, ct);

    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languages, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var language = languages.Select(ProviderConnection.Language).OfType<string>().FirstOrDefault() ?? Connection.Get("liveLanguage", "en");
        if (!SupportedLanguages.Contains(language))
            throw new PluginRequestException("Select a supported language for Speechmatics live transcription.", PluginRequestFailureKind.Configuration);
        var config = new Dictionary<string, object>
        {
            ["language"] = language, ["model"] = SelectedModelId!,
            ["enable_partials"] = true, ["max_delay"] = 0.7
        };
        var terms = ProviderConnection.Terms(prompt);
        if (terms.Length > 0) config["additional_vocab"] = terms.Select(content => new { content }).ToArray();
        var endpoint = new Uri(Connection.Get("region", "eu") == "us" ? "wss://us.rt.speechmatics.com/v2" : "wss://eu.rt.speechmatics.com/v2");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await ConnectStreaming(endpoint, Connection.RequireKey(), language, config, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new PluginRequestException("Speechmatics live connection timed out.", PluginRequestFailureKind.Timeout); }
    }
}
