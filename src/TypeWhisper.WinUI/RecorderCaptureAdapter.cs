using Microsoft.UI.Dispatching;
using TypeWhisper.Presentation;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

internal sealed class RecorderCaptureAdapter : IDisposable
{
    private readonly AudioRecordingService _microphone;
    private readonly DispatcherQueue _dispatcher;
    private readonly SystemAudioCaptureService _system = new();
    private readonly RecorderSourceCoordinator _sources;
    private float _micLevel;
    private float _systemLevel;
    private int _generation;
    private float[]? _pendingMicrophone;
    private readonly System.Diagnostics.Stopwatch _timeline = new();
    private TimeSpan? _stopAt;
    internal float Level => Math.Max(_micLevel, _systemLevel);
    internal string? Warning { get; private set; }
    internal RecorderCaptureAdapter(AudioRecordingService microphone, DispatcherQueue dispatcher)
    {
        _microphone = microphone; _dispatcher = dispatcher;
        _sources = new(() =>
        {
            microphone.WhisperModeEnabled = false;
            microphone.StartRecording(enableRecovery: false);
            if (!microphone.IsRecording) throw new InvalidOperationException("The microphone could not start. Check microphone access and your input device.");
            return Task.CompletedTask;
        }, () =>
        {
            _system.StartCapture(timelineOffset: _timeline.Elapsed);
            if (!_system.IsRecording) throw new InvalidOperationException("System audio capture could not start.");
            return Task.CompletedTask;
        }, async () =>
        {
            try
            {
                _pendingMicrophone ??= await microphone.StopRecordingAsync() ?? [];
                microphone.RetryFailedCaptureCleanup();
                var samples = _pendingMicrophone; _pendingMicrophone = null;
                return samples;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && (microphone.IsRecording || microphone.HasUnreleasedCapture))
            { throw new RecorderCleanupException("Microphone cleanup failed. Retry stopping before starting another recording.", ex); }
        }, () =>
        {
            try { return Task.FromResult(_system.StopCapture(_stopAt)); }
            catch (Exception ex) when (ex is not OutOfMemoryException && _system.HasCaptureResources)
            { throw new RecorderCleanupException("System audio cleanup failed. Retry stopping before starting another recording.", ex); }
        });
    }
    internal async Task StartAsync(bool microphone, bool system)
    {
        _generation++;
        Warning = null;
        _timeline.Restart();
        _stopAt = null;
        _microphone.AudioLevelChanged += MicLevel;
        _system.AudioLevelChanged += SystemLevel;
        try { await _sources.StartAsync(microphone, system); }
        catch { Unsubscribe(); throw; }
    }
    internal async Task<float[]> StopAsync()
    {
        _stopAt ??= _timeline.Elapsed;
        try
        {
            var captured = await _sources.StopAsync();
            Warning = captured.Warnings.Count == 0 ? null : string.Join(" ", captured.Warnings) + " Available audio was retained.";
            return await Task.Run(() => RecorderMixer.MixForOutput(captured.Microphone, captured.System, RecorderMicDuckingMode.Off));
        }
        finally { Unsubscribe(); }
    }
    private void Unsubscribe()
    {
        _generation++;
        _microphone.AudioLevelChanged -= MicLevel;
        _system.AudioLevelChanged -= SystemLevel;
        _micLevel = _systemLevel = 0;
    }
    private void MicLevel(object? sender, AudioLevelEventArgs value)
    {
        var generation = _generation;
        _dispatcher.TryEnqueue(() => { if (generation == _generation) _micLevel = Math.Clamp(value.PeakLevel, 0, 1); });
    }
    private void SystemLevel(float value)
    {
        var generation = _generation;
        _dispatcher.TryEnqueue(() => { if (generation == _generation) _systemLevel = Math.Clamp(value, 0, 1); });
    }
    public void Dispose() { Unsubscribe(); _system.Dispose(); }
}
