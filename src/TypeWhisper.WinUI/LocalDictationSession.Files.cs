using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private bool _fileBusy;
    internal string? FileProcessingStatus { get; private set; }
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _fileDispatcher;
    internal bool CanTranscribeFile => CanChangeProvider && IsReady && !Models.Busy;

    internal Task<FileTranscriptionOutput> TranscribeFileAsync(string path, Action<string> stage, CancellationToken ct) =>
        TranscribeFileAsync(path, stage, ct, null);

    internal async Task<FileTranscriptionOutput> TranscribeFileAsync(string path, Action<string> stage, CancellationToken ct, ParsedApiTranscription? apiRequest)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanTranscribeFile || !await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Finish the current recording or model operation before transcribing a file.");
        _fileBusy = true;
        FileProcessingStatus = apiRequest is null ? "Loading audio · open Files for progress or cancellation" : "Processing HTTP API request";
        void Report(string message)
        {
            void Publish()
            {
                if (ct.IsCancellationRequested) return;
                FileProcessingStatus = message + (apiRequest is null ? " · Files" : " · HTTP API");
                stage(message); Changed?.Invoke();
            }
            if (_fileDispatcher.HasThreadAccess) Publish();
            else _fileDispatcher.TryEnqueue(Publish);
        }
        try
        {
            ct = _operationCancellation.Begin(ct);
            Changed?.Invoke();
            var engineId = ActiveEngineId;
            var modelId = ActiveModelId;
            var modelName = ActiveModelName;
            var providerSelection = RegistrySelectionId(_providerId);
            var registryProvider = UsesRegistryProvider;
            if (apiRequest?.Model is { } requestedModel && requestedModel != modelId && requestedModel != ActiveChoiceId &&
                requestedModel != _providerId + ":" + modelId)
                throw new LocalApiRequestException(409, "The requested model is not selected. Select it in Dictation first.");
            if (apiRequest?.Engine is { } requestedEngine && requestedEngine != engineId && requestedEngine != _providerId &&
                requestedEngine != providerSelection)
                throw new LocalApiRequestException(409, "The requested engine is not selected.");
            var language = apiRequest?.Language ?? Language;
            if (apiRequest?.Language is { } requestedLanguage && requestedLanguage != "auto" &&
                !SupportedLanguages.Contains(requestedLanguage, StringComparer.OrdinalIgnoreCase))
                throw new LocalApiRequestException(422, "The selected model does not support this language.");
            if (apiRequest?.Language is not null && language != "auto")
                language = SupportedLanguages.First(code => code.Equals(language, StringComparison.OrdinalIgnoreCase));
            var task = apiRequest?.Task is { } requestedTask
                ? requestedTask == "translate" ? TranscriptionTask.Translate : TranscriptionTask.Transcribe
                : TranscriptionTaskPreferences.Current;
            var textPreferences = TextPreferences.Current;
            var processors = PluginRuntime.PostProcessors.ToArray();
            var outputPreferences = OutputPreferences.Current;
            var ctcReady = CtcVocabulary.Enabled;
            var boostVocabulary = DictionaryBoostingPreferences.Load();
            var lexicon = await Task.Run(() => DictationLexiconSnapshot.Load(
                DictationDictionarySnapshot.StoragePath, DictationSnippetSnapshot.StoragePath), ct);
            var translate = task == TranscriptionTask.Translate;
            if (translate && !SupportsTranslation)
                throw new NotSupportedException("This model cannot translate audio to English. Choose Transcribe or a translation-capable model.");
            await _livePreview.StopAsync();
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            Report("Loading audio…");
            float[] samples;
            try { samples = await MediaFoundationFileDecoder.LoadAsync(path, ct); }
            catch (Exception ex) when (apiRequest is not null && ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            { throw new LocalApiRequestException(422, "The audio could not be decoded. Use supported media up to 60 minutes."); }
            ct.ThrowIfCancellationRequested();
            Report($"Transcribing with {modelName}…");
            var decoded = registryProvider
                ? await PluginRuntime.UseTranscriptionAsync(providerSelection, (engine, token) =>
                    LanguageHintTranscription.DecodeAsync(engine, samples,
                        () => PcmWaveEncoder.Encode(samples, engine.MaximumAudioUploadBytes),
                        language == "auto" ? null : language,
                        textPreferences.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries), translate, token), ct)
                : await _transcriptionPlugin.DecodeResultAsync(samples, language == "auto" ? null : language, translate, ct);
            // Keep the gate until non-interruptible native work has actually drained.
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (FinalSpeechPolicy.ShouldReject(decoded.Text, decoded.NoSpeechProbability, false,
                textPreferences.TranscribeShortQuietClipsAggressively) || string.IsNullOrWhiteSpace(decoded.Text))
                throw new InvalidOperationException("No speech was recognized in this file.");
            Report("Formatting transcript…");
            var refinedText = decoded.Text;
            var useCtc = DictationLexiconSnapshot.CanRefineWithCtc(task, registryProvider, modelId,
                ctcReady && CtcVocabulary.Enabled, decoded.TokenTimings.Count);
            var ctcWarnings = new List<string>();
            if (useCtc && lexicon.Dictionary is { EnabledCtcEntries.Count: > 0 } dictionary)
            {
                Report("Checking vocabulary with CTC…");
                var refined = await CtcVocabulary.RefineAsync(Guid.NewGuid(), decoded.Text, samples,
                    decoded.TokenTimings, dictionary.EnabledCtcEntries, ct);
                refinedText = refined.Text;
                if (refined.Error is not null) ctcWarnings.Add("Acoustic vocabulary checking was unavailable. The decoded transcript was retained.");
            }
            var processed = await lexicon.ProcessAsync(refinedText, textPreferences, language,
                DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, language), boostVocabulary && !useCtc,
                apiRequest is null ? ReadSnippetClipboardAsync : _ => Task.FromResult(""), ct, task, null, engineId, modelId, textProcessors:
                    BindTextProcessors(processors, DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, language),
                        null, null, samples.Length / 16000.0));
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var warnings = processed.Warnings.ToList();
            if (string.IsNullOrWhiteSpace(processed.Text))
                throw new InvalidOperationException("Text processing produced an empty transcript.");
            warnings.AddRange(ctcWarnings);
            var duration = samples.Length / 16000.0;
            TranscriptionRecord? pendingHistory = null;
            if (apiRequest is null && outputPreferences.RestrictedBy(OutputPreferences.Current).SaveToHistory)
            {
                Report("Preparing History…");
                try
                {
                    await _history.EnsureLoadedAsync();
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (apiRequest is null && outputPreferences.RestrictedBy(OutputPreferences.Current).SaveToHistory) pendingHistory = new()
                    {
                        Id = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                        TextProcessors = processed.TextProcessors?.ToArray(),
                        Status = processed.TextProcessors?.Any(item => item.Status == "failed") == true ? TranscriptionRecordStatus.TextProcessorFailed : TranscriptionRecordStatus.Succeeded,
                        SourceKind = "file", RawText = decoded.Text, FinalText = processed.Text, DurationSeconds = duration,
                        EngineUsed = engineId, ModelUsed = modelId,
                        TranscriptionTaskUsed = translate ? "translate" : "transcribe",
                        Language = DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, language)
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (ObjectDisposedException) when (_disposed) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { warnings.Add("History could not be saved. Your transcript remains available here for export."); }
            }
            return new(processed.Text, engineId, modelId ?? modelName, duration,
                decoded.Segments.Select(segment => new TranscriptionSegment(segment.Text, segment.Start, segment.End)).ToArray(),
                warnings.Count == 0 ? null : string.Join(" · ", warnings))
            {
                DisplayName = registryProvider ? modelName : "NVIDIA · " + modelName,
                AppliedSnippetIds = processed.AppliedSnippetIds,
                PendingHistory = pendingHistory
            };
        }
        finally
        {
            _fileBusy = false;
            FileProcessingStatus = null;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    internal string? AcceptFileResult(FileTranscriptionOutput result) => FileTranscriptionAcceptance.Commit(
        result, _history, OutputPreferences.Current,
        ids => DictationLexiconSnapshot.RecordUsage(DictationSnippetSnapshot.StoragePath, ids));

    private static async Task<string> ReadSnippetClipboardAsync(CancellationToken ct)
    {
        var clipboard = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        return clipboard.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
            ? await clipboard.GetTextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct) : "";
    }
}
