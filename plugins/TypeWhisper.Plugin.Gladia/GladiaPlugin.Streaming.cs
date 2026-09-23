using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Gladia;

public sealed partial class GladiaPlugin
{
    /// <inheritdoc />
    public bool SupportsStreaming => true;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAsync(language is null ? [] : [language], ct);
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);
    /// <inheritdoc />
    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Connection.Request(HttpMethod.Post, "https://api.gladia.io/v2/live", header: "x-gladia-key");
        request.Content = ProviderConnection.Json(StreamingConfiguration(languageHints, prompt));
        using var response = await Connection.ReadAsync(request, timeout.Token).ConfigureAwait(false);
        var uri = StreamingEndpoint(ProviderConnection.RequiredText(response.RootElement, "url"));
        return await ConnectStreaming(uri, timeout.Token).ConfigureAwait(false);
    }

    internal Func<Uri, CancellationToken, Task<IStreamingSession>> ConnectStreaming { get; set; } = GladiaStreamingSession.ConnectAsync;

    internal static Uri StreamingEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss"
            || !uri.Host.Equals("api.gladia.io", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/v2/live")
            throw ProviderConnection.InvalidResponse();
        return uri;
    }

    internal static object StreamingConfiguration(IReadOnlyList<string> hints, string? prompt)
    {
        var languages = hints.Select(ProviderConnection.Language).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var configuration = new Dictionary<string, object>
        {
            ["model"] = "solaria-1", ["encoding"] = "wav/pcm", ["sample_rate"] = 16000,
            ["bit_depth"] = 16, ["channels"] = 1,
            ["language_config"] = new { languages, code_switching = languages.Length > 1 },
            ["messages_config"] = new
            {
                receive_partial_transcripts = true, receive_final_transcripts = true,
                receive_lifecycle_events = true, receive_errors = true, receive_acknowledgments = true
            }
        };
        var terms = ProviderConnection.Terms(prompt);
        if (terms.Length > 0) configuration["realtime_processing"] = new
        {
            custom_vocabulary = true,
            custom_vocabulary_config = new { vocabulary = terms, default_intensity = 0.7 }
        };
        return configuration;
    }
}
