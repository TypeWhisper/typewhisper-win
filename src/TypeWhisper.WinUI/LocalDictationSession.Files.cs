using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private bool _fileBusy;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _fileDispatcher;
    internal bool CanTranscribeFile => CanChangeProvider && IsReady && !Models.Busy;

    internal async Task<FileTranscriptionOutput> TranscribeFileAsync(string path, Action<string> stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanTranscribeFile || !await _gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Finish the current recording or model operation before transcribing a file.");
        _fileBusy = true;
        void Report(string message)
        {
            if (_fileDispatcher.HasThreadAccess) stage(message);
            else _fileDispatcher.TryEnqueue(() => { if (!ct.IsCancellationRequested) stage(message); });
        }
        try
        {
            Changed?.Invoke();
            var engineId = ActiveEngineId;
            var modelId = ActiveModelId;
            var modelName = ActiveModelName;
            var providerSelection = RegistrySelectionId(_providerId);
            var registryProvider = UsesRegistryProvider;
            var language = Language;
            var task = TranscriptionTaskPreferences.Current;
            var textPreferences = TextPreferences.Current;
            var outputPreferences = OutputPreferences.Current;
            var translate = task == TranscriptionTask.Translate;
            if (translate && !SupportsTranslation)
                throw new NotSupportedException("This model cannot translate audio to English. Choose Transcribe or a translation-capable model.");
            await _livePreview.StopAsync();
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            Report("Loading audio…");
            var samples = await MediaFoundationFileDecoder.LoadAsync(path, ct);
            ct.ThrowIfCancellationRequested();
            Report($"Transcribing with {modelName}…");
            var decoded = registryProvider
                ? await PluginRuntime.UseTranscriptionAsync(providerSelection, (engine, token) =>
                    LanguageHintTranscription.DecodeAsync(engine, samples,
                        () => CloudTranscriptionPlugin.EncodeWav(samples,
                            engine.ProviderId == "groq" ? 25_000_000 : int.MaxValue),
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
            var processed = await DictationTextPipeline.ProcessAsync(decoded.Text, textPreferences, language,
                detectedLanguage: DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, language),
                ct: ct, task: task, engineId: engineId, modelId: modelId);
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var warnings = processed.Warnings.ToList();
            var duration = samples.Length / 16000.0;
            if (outputPreferences.RestrictedBy(OutputPreferences.Current).SaveToHistory)
            {
                Report("Saving to History…");
                try
                {
                    await _history.EnsureLoadedAsync();
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (outputPreferences.RestrictedBy(OutputPreferences.Current).SaveToHistory && !_history.TryAddRecord(new()
                    {
                        Id = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                        SourceKind = "file", RawText = decoded.Text, FinalText = processed.Text, DurationSeconds = duration,
                        EngineUsed = engineId, ModelUsed = modelId,
                        TranscriptionTaskUsed = translate ? "translate" : "transcribe",
                        Language = DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, language)
                    })) warnings.Add("History could not be saved. Your transcript remains available here for export.");
                }
                catch (OperationCanceledException) { throw; }
                catch (ObjectDisposedException) when (_disposed) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { warnings.Add("History could not be saved. Your transcript remains available here for export."); }
            }
            return new(processed.Text, engineId, modelId ?? modelName, duration,
                decoded.Segments.Select(segment => new TranscriptionSegment(segment.Text, segment.Start, segment.End)).ToArray(),
                warnings.Count == 0 ? null : string.Join(" · ", warnings));
        }
        finally
        {
            _fileBusy = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }
}
