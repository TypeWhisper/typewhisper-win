using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// A spoken input for modules. It reserves the session like the recorder, so a dictation shortcut, the recorder, file
// transcription and model changes are refused while it runs, and it never shows the overlay or plays sounds. It
// records with the dictation microphone and transcribes with the dictation engine under the same policy, then applies
// the dictionary and text settings without snippets, workflows, text processors, History or insertion.
internal sealed partial class LocalDictationSession
{
    // True from the microphone opening until the reservation is released.
    private bool _speechInputActive;

    internal ISpeechInputRuntime CreateSpeechInputRuntime() => new SpeechInputRuntime(this);

    private sealed class SpeechInputRuntime(LocalDictationSession owner) : ISpeechInputRuntime
    {
        private readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();

        public TimeSpan Now => System.Diagnostics.Stopwatch.GetElapsedTime(_origin);
        public bool Closing => owner._disposed;
        public string? Refusal() => owner.TranscriptionPolicyRefusal();

        public Task<ISpeechInputCapture> OpenAsync(Action<float> level, CancellationToken cancellationToken) =>
            owner.OpenSpeechInputAsync(level, cancellationToken);

        public IDisposable Every(TimeSpan interval, Action tick)
        {
            var timer = owner._fileDispatcher.CreateTimer();
            timer.Interval = interval;
            timer.Tick += (_, _) => tick();
            timer.Start();
            return new Ticks(timer);
        }

        private sealed class Ticks(Microsoft.UI.Dispatching.DispatcherQueueTimer timer) : IDisposable
        {
            public void Dispose() => timer.Stop();
        }
    }

    private async Task<ISpeechInputCapture> OpenSpeechInputAsync(Action<float> level, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Failed, Loc.T("The app is closing."));
        // Running work is reported as busy before the readiness it may be changing, such as a model that loads.
        if (!CanChangeProvider || Models.Busy || SpokenFeedback.IsBusy || _gate.CurrentCount == 0)
            throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Busy, ModuleSpeechInput.BusyMessage);
        if (TranscriptionPolicyRefusal() is { } refused) throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Refused, refused);
        if (!IsReady)
            throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.NotSetUp,
                Loc.T("No model is ready. Download a model or configure a cloud provider in plugin settings, then select it in Dictation."));
        IDisposable reservation;
        try { reservation = ReserveRecorder(); }
        catch (InvalidOperationException busy) { throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.Busy, ModuleSpeechInput.BusyMessage, busy); }
        SpeechInputCapture? capture = null;
        try
        {
            _speechInputActive = true;
            // Linked to the session's operation scope, so shutdown and a policy change stop the input like a dictation.
            var token = _operationCancellation.Begin(cancellationToken);
            await _livePreview.StopAsync();
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            capture = new SpeechInputCapture(this, reservation, level, token);
            AppDiagnostics.Write($"modules.speech-input.capture engine={ActiveEngineId} model={ActiveModelId}");
            capture.Start();
            return capture;
        }
        catch
        {
            if (capture is not null) await capture.DisposeAsync();
            else { _speechInputActive = false; reservation.Dispose(); }
            throw;
        }
    }

    private sealed class SpeechInputCapture : ISpeechInputCapture
    {
        private readonly LocalDictationSession _owner;
        private readonly IDisposable _reservation;
        private readonly Action<float> _level;
        // The engine, model and text settings at the start, as a dictation keeps them.
        private readonly bool _registryProvider;
        private readonly string _providerSelection;
        private readonly string _engineId;
        private readonly string? _modelId;
        private readonly string _language;
        private readonly IReadOnlyList<string> _hints;
        private readonly DictationTextPreferences _text;
        private readonly bool _boostVocabulary;
        private readonly bool _rescoresWithCtc;
        private Task<DictationDictionarySnapshot?>? _dictionary;
        private bool _subscribed;
        private Task? _disposal;

        internal SpeechInputCapture(LocalDictationSession owner, IDisposable reservation, Action<float> level, CancellationToken cancellation)
        {
            _owner = owner;
            _reservation = reservation;
            _level = level;
            Cancellation = cancellation;
            _registryProvider = owner.UsesRegistryProvider;
            _providerSelection = RegistrySelectionId(owner._providerId);
            _engineId = owner.ActiveEngineId;
            _modelId = owner.ActiveModelId;
            _language = owner.Language;
            _text = owner.TextPreferences.Current;
            _hints = _text.PreferredLanguageHints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            _boostVocabulary = DictionaryBoostingPreferences.Load();
            _rescoresWithCtc = !_registryProvider && ParakeetModels.IsParakeetTdt(_modelId);
        }

        public CancellationToken Cancellation { get; }
        public bool Recording => _owner._audio.IsRecording;
        public float Level => _owner._audio.CurrentRmsLevel;
        public bool RecognizeQuietClips => _text.TranscribeShortQuietClipsAggressively;

        internal void Start()
        {
            var audio = _owner._audio;
            // The user's microphone gain for dictation applies; recovery audio is for dictations only.
            audio.WhisperModeEnabled = _owner.AudioPreferences.WhisperModeEnabled;
            audio.AudioLevelChanged += OnLevel;
            _subscribed = true;
            audio.StartRecording(enableRecovery: false);
            if (!audio.IsRecording)
                throw new ModuleSpeechInputException(ModuleSpeechInputErrorKind.MicrophoneUnavailable,
                    _owner.MicrophoneNotice() ?? MicrophoneFailure.Generic);
            // A model released after inactivity loads while the user speaks; so does the dictionary.
            _owner.PrepareTranscriptionModel(_rescoresWithCtc);
            _dictionary = Task.Run(LoadDictionary);
        }

        public async Task<SpeechInputAudio> StopAsync()
        {
            Unsubscribe();
            var peak = _owner._audio.PreGainPeakRmsLevel;
            var samples = _owner._audio.IsRecording ? await _owner._audio.StopRecordingAsync() : null;
            return new(samples, peak);
        }

        public async Task<SpeechInputTranscript> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
        {
            var dictionary = _dictionary is null ? null : await _dictionary;
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = await _owner.DecodeSamplesAsync(ShortClipCapturePolicy.PadForFinalDecode(samples), _registryProvider, _providerSelection,
                _language, _hints, false, dictionary?.EnabledTerms, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);
            if (string.IsNullOrWhiteSpace(decoded.Text)
                || FinalSpeechPolicy.ShouldReject(decoded.Text, decoded.NoSpeechProbability, false, _text.TranscribeShortQuietClipsAggressively))
                return new("", null);
            var language = DictationProvenance.ResolveLanguage(decoded.DetectedLanguage, _language);
            var refined = decoded.Text;
            var useCtc = DictationLexiconSnapshot.CanRefineWithCtc(TranscriptionTask.Transcribe, _registryProvider, _modelId,
                _owner.CtcVocabulary.Enabled, decoded.TokenTimings.Count);
            if (useCtc && dictionary is { EnabledCtcEntries.Count: > 0 } terms)
            {
                var outcome = await _owner.CtcVocabulary.RefineAsync(Guid.NewGuid(), decoded.Text, samples, decoded.TokenTimings, terms.EnabledCtcEntries,
                    cancellationToken);
                refined = outcome.Text;
                if (outcome.Error is not null) AppDiagnostics.Write("modules.speech-input.ctc-unavailable");
            }
            var text = await ModuleSpeechInput.ProcessTextAsync(refined, dictionary, _text, _language, language, _boostVocabulary && !useCtc,
                _engineId, _modelId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(text, language);
        }

        public ValueTask DisposeAsync() => new(_disposal ??= CloseAsync());

        private async Task CloseAsync()
        {
            Unsubscribe();
            try
            {
                // A recording that did not reach the transcription is discarded.
                if (_owner._audio.IsRecording) await _owner._audio.StopRecordingAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.WriteFailure("modules.speech-input.stop-failed", ex); }
            finally
            {
                _owner._speechInputActive = false;
                _reservation.Dispose();
            }
        }

        private void OnLevel(object? sender, AudioLevelEventArgs level) => _level(level.RmsLevel);

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            _owner._audio.AudioLevelChanged -= OnLevel;
        }

        // A dictionary that cannot be read leaves the transcript as the engine returned it.
        private static DictationDictionarySnapshot? LoadDictionary()
        {
            try { return DictationDictionarySnapshot.Load(DictationDictionarySnapshot.StoragePath); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppDiagnostics.WriteFailure("modules.speech-input.dictionary-failed", ex);
                return null;
            }
        }
    }
}
