using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private async Task StartCloudStreamAsync()
    {
        if (!LivePreviewEnabled || !SupportsLiveTranscription || !UsesRegistryProvider ||
            ActiveRegistryProvider is not { SupportsStreaming: true }) return;
        var selection = RegistrySelectionId(_providerId);
        var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
        var canStream = await PluginRuntime.UseTranscriptionAsync(selection, (engine, _) =>
            Task.FromResult(engine.SupportsStreamingForPrompt(LanguageHintTranscription.CreateDictionaryPrompt(engine, dictionary?.EnabledTerms))), _operationCancellation.Token);
        if (!canStream) return;
        var languages = _languageAtStart != "auto" ? new[] { _languageAtStart }
            : ActiveRegistryProvider.SupportsLanguageHints
                ? _textAtStart.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();
        StreamingDictation? stream = null;
        stream = new StreamingDictation((use, ct) => PluginRuntime.UseTranscriptionAsync(selection, use, ct),
            languages, text => _fileDispatcher.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(_cloudStream, stream) || !_audio.IsRecording) return;
                _hasConfirmedPreviewText |= !string.IsNullOrWhiteSpace(text);
                LivePreviewText = text;
                LivePreviewChanged?.Invoke();
            }), () => _fileDispatcher.TryEnqueue(() =>
            {
                if (_disposed || !ReferenceEquals(_cloudStream, stream) || !_audio.IsRecording) return;
                LivePreviewText = "Live connection interrupted. The full recording will be transcribed after stopping.";
                LivePreviewChanged?.Invoke();
            }), _operationCancellation.Token, dictionary?.EnabledTerms);
        _cloudStream = stream;
    }

    private async Task StopCloudStreamAsync()
    {
        var previous = _cloudStream;
        _cloudStream = null;
        if (previous is not null) await previous.DisposeAsync();
    }
}
