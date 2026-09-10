using TypeWhisper.Core.Services;
using TypeWhisper.PluginHost;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private TrainingCapture? _training;
    internal TrainingCapture BeginWordTraining()
    {
        if (!IsReady) throw new InvalidOperationException("Select a ready model in Dictation before training a word.");
        var reservation = ReserveRecorder();
        try { return _training = new TrainingCapture(this, reservation); }
        catch { reservation.Dispose(); throw; }
    }

    internal sealed class TrainingCapture(LocalDictationSession owner, IDisposable reservation) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private Task<string>? _decode;
        private Task? _disposal;
        private bool _recording;
        internal string Language { get; set; } = owner.Language;
        internal string ModelName => owner.ActiveModelName;
        internal float Level => _recording ? owner.CurrentLevel : 0;

        internal void Start()
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_recording || _decode is { IsCompleted: false }) throw new InvalidOperationException("Finish the current sample first.");
            owner._audio.StartRecording(enableRecovery: false);
            if (!owner._audio.IsRecording) throw new InvalidOperationException("Microphone could not start. Check the selected input and microphone access.");
            _recording = true;
        }

        internal Task<string> StopAsync()
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (!_recording) throw new InvalidOperationException("Record a sample first.");
            _recording = false;
            return _decode = DecodeAsync();
        }

        private async Task<string> DecodeAsync()
        {
            var ct = _cancel.Token;
            var samples = await owner._audio.StopRecordingAsync();
            ct.ThrowIfCancellationRequested();
            if (samples is not { Length: > 0 }) throw new InvalidOperationException("No audio was captured. Please record the sentence again.");
            samples = ShortClipCapturePolicy.PadForFinalDecode(samples);
            // Deliberately bypass dictionary hints, CTC, snippets, formatting, workflows and History.
            var language = Language == "auto" ? null : Language;
            var result = owner.UsesRegistryProvider
                ? await owner.PluginRuntime.UseTranscriptionAsync(RegistrySelectionId(owner._providerId), (engine, token) =>
                    LanguageHintTranscription.DecodeAsync(engine, samples, () => PcmWaveEncoder.Encode(samples, engine.MaximumAudioUploadBytes),
                        language, [], false, token), ct)
                : await owner._transcriptionPlugin.DecodeResultAsync(samples, language, false, ct);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidOperationException("No speech was recognized. Please record the sentence again.");
            return result.Text;
        }

        public ValueTask DisposeAsync() => new(_disposal ??= CloseAsync());
        private async Task CloseAsync()
        {
            _cancel.Cancel();
            try
            {
                if (_recording) { _recording = false; await owner._audio.StopRecordingAsync(); }
                if (_decode is not null)
                {
                    try { await _decode; }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("Training ended: " + ex.GetType().Name); }
                }
            }
            finally
            {
                _cancel.Dispose();
                owner._training = null;
                reservation.Dispose();
            }
        }
    }
}
