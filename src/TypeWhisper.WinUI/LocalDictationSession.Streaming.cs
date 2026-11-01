using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly TypeWhisper.Presentation.BufferedAudioHandoff _streamAudio = new();

    private async Task StartCloudStreamAsync()
    {
        if (!LivePreviewEnabled || !SupportsLiveTranscription || !UsesRegistryProvider ||
            ActiveRegistryProvider is not { SupportsStreaming: true }) return;
        var selection = RegistrySelectionId(_providerId);
        var dictionary = _dictionarySnapshot is null ? null : await _dictionarySnapshot;
        var terms = PromptTermsFor(selection, dictionary?.EnabledTerms);
        // Judged again under the lease: the wait for the gate may have outlasted a text-policy change.
        // A provider that cannot stream with the dictionary prompt may still offer a preview stream; the final
        // text then comes from the complete recording with the dictionary (see StreamingDictation.IsPreviewOnly).
        var canStream = await PluginRuntime.UseTranscriptionAsync(selection, (engine, _) =>
        {
            var prompt = LanguageHintTranscription.CreateDictionaryPrompt(engine, PromptTermsFor(selection, terms));
            return Task.FromResult(engine.SupportsStreamingForPrompt(prompt) || engine.SupportsStreamingPreviewForPrompt(prompt));
        }, _operationCancellation.Token);
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
                LivePreviewText = Loc.T("Live connection interrupted. The full recording will be transcribed after stopping.");
                LivePreviewChanged?.Invoke();
            }), _operationCancellation.Token, terms, promptAllowed: () => PluginRuntime.TranscriptionPromptRefusal(selection) is null);
        _cloudStream = stream;
        if (!_streamAudio.Attach(samples => stream.Append(samples)))
        {
            // A bounded startup-buffer overflow falls back to the complete captured recording.
            await StopCloudStreamAsync();
        }
    }

    private async Task StopCloudStreamAsync()
    {
        _streamAudio.Reset();
        var previous = _cloudStream;
        _cloudStream = null;
        if (previous is not null) await previous.DisposeAsync();
    }
}
