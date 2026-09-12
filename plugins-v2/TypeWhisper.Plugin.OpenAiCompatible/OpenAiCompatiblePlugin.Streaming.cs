using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
{
    /// <inheritdoc />
    public bool SupportsStreaming => UsesRealtime(RequireProfile(DefaultProfileId));
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsLanguageHints => SupportsStreaming && CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? "");
    /// <inheritdoc />
    public bool SupportsDictionaryTerms => !SupportsStreaming || CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? "");
    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) => SupportsStreaming &&
        (string.IsNullOrWhiteSpace(prompt) || CompatibleRealtimeStreamingSession.IsLiveModel(SelectedModelId ?? ""));
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartProfileStreamingAsync(DefaultProfileId, LanguageHints(language), null, ct);
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(IReadOnlyList<string> languageHints, CancellationToken ct) =>
        StartProfileStreamingAsync(DefaultProfileId, languageHints, null, ct);
    /// <inheritdoc />
    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(IReadOnlyList<string> languageHints, string? prompt, CancellationToken ct) =>
        StartProfileStreamingAsync(DefaultProfileId, languageHints, prompt, ct);
    /// <inheritdoc />
    public Task<TypeWhisper.PluginSDK.Models.PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(byte[] wavAudio,
        IReadOnlyList<string> languageHints, bool translate, string? prompt, CancellationToken ct) =>
        TranscribeProfileWithHintsAsync(DefaultProfileId, wavAudio, languageHints, translate, prompt, ct);

    private static IReadOnlyList<string> LanguageHints(string? language) =>
        string.IsNullOrWhiteSpace(language) || language == "auto" ? [] : [language.Trim()];

    private async Task<IStreamingSession> StartProfileStreamingAsync(string profileId, IReadOnlyList<string> hints, string? prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = RequireProfile(profileId);
        if (!UsesRealtime(profile)) throw new NotSupportedException("This profile uses batch transcription.");
        if (string.IsNullOrWhiteSpace(profile.SelectedModelId)) throw new PluginRequestException("Select a transcription model.", PluginRequestFailureKind.Configuration);
        return await CompatibleRealtimeStreamingSession.ConnectAsync(RealtimeUri(profile), GetApiKey(profileId) ?? "", profile.SelectedModelId,
            NormalizeHints(hints), prompt, ct);
    }

    private async Task<TypeWhisper.PluginSDK.Models.PluginTranscriptionResult> TranscribeProfileWithHintsAsync(string profileId, byte[] audio,
        IReadOnlyList<string> hints, bool translate, string? prompt, CancellationToken ct)
    {
        var profile = RequireProfile(profileId);
        if (!UsesRealtime(profile)) return await TranscribeAsync(profileId, audio, NormalizeHints(hints).FirstOrDefault(), translate, prompt, ct);
        if (translate) throw new PluginRequestException("Realtime transcription does not support translation. Use batch mode.", PluginRequestFailureKind.Configuration);
        if (string.IsNullOrWhiteSpace(profile.SelectedModelId)) throw new PluginRequestException("Select a transcription model.", PluginRequestFailureKind.Configuration);
        using var timeout = CreateRequestTimeoutSource(ct, DefaultHttpRequestTimeout);
        return await CompatibleRealtimeStreamingSession.TranscribeWavAsync(RealtimeUri(profile), GetApiKey(profileId) ?? "", profile.SelectedModelId,
            audio, NormalizeHints(hints), prompt, timeout.Token);
    }

    private static string[] NormalizeHints(IReadOnlyList<string> hints) => hints.Where(h => !string.IsNullOrWhiteSpace(h) && h != "auto")
        .Select(h => h.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
