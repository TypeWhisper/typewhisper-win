using System.Runtime.InteropServices;
using SherpaOnnx;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

// Initial local vertical slice: reuses the existing capture implementation and
// Parakeet configuration. Does not instantiate the WPF application or plugin UI.
internal sealed class LocalDictationSession : IDisposable
{
    private readonly AudioRecordingService _audio = new();
    private readonly SoundService _sounds = new();
    private readonly AudioDuckingService _ducking = new();
    private readonly RecordingAudioEffects _effects;
    private readonly LocalLivePreview _livePreview = new();
    internal bool LivePreviewEnabled { get; set; } = true;
    internal string LivePreviewText { get; private set; } = "";
    internal event Action? LivePreviewChanged;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _silenceTimer;
    private volatile SilenceAutoStop? _silence;
    private readonly System.Diagnostics.Stopwatch _silenceClock = new();
    internal DictationAudioPreferences AudioPreferences { get; private set; } = new();
    internal string? AudioPreferencesError { get; private set; }
    private static readonly string AudioPreferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "audio.json");

    internal string? SaveAudioPreferences(DictationAudioPreferences preferences)
    {
        try
        {
            preferences = preferences.Validated();
            Directory.CreateDirectory(Path.GetDirectoryName(AudioPreferencesPath)!);
            File.WriteAllText(AudioPreferencesPath + ".tmp", System.Text.Json.JsonSerializer.Serialize(preferences));
            File.Move(AudioPreferencesPath + ".tmp", AudioPreferencesPath, true);
            AudioPreferences = preferences;
            AudioPreferencesError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return AudioPreferencesError = "Could not save audio preferences: " + ex.Message; }
    }
    private readonly IHistoryService _history;
    private readonly ClipboardTextInserter _inserter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private IntPtr _target;
    private DateTime _started;
    private bool _disposed;
    private DictationPhase _phase;
    private TimeSpan _lastDuration;
    private string _targetApp = "";
    private uint _targetProcessId;
    internal DictationOverlayState OverlayState => new(_phase,
        _audio.IsRecording ? _audio.RecordingDuration : _lastDuration, Status, _targetApp, _targetProcessId);
    internal string Status { get; private set; } = "Loading Parakeet…";
    internal string Shortcut { get; set; } = "Ctrl+Shift+F9";
    internal bool IsRecording => _audio.IsRecording;
    internal bool IsReady => _recognizer is not null;
    internal float CurrentLevel => _audio.CurrentRmsLevel;
    internal event Action? Changed;
    private List<MicrophonePriorityItem> _microphones = [];
    private static readonly string MicrophonePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeWhisper-WinUI-DevUserData", "microphone.json");
    internal IReadOnlyList<MicrophonePriorityItem> MicrophonePriority => _microphones.AsReadOnly();
    internal string SelectedMicrophoneId => _microphones.FirstOrDefault()?.Id ?? "default";
    internal string SelectedMicrophoneName => _microphones.FirstOrDefault()?.Name ?? "System default";
    internal IReadOnlyList<AudioInputDeviceInfo> GetMicrophones() => _audio.GetAvailableInputDeviceInfos();

    internal string? SelectMicrophone(string id)
    {
        var device = GetMicrophones().FirstOrDefault(item => item.Id == id);
        if (id != "default" && device is null) return "This microphone is no longer available. Reopen Audio to refresh.";
        return SetMicrophonePriority(device is null ? [] :
            new[] { new MicrophonePriorityItem(device.Id, device.Name) }.Concat(_microphones.Where(item => item.Id != id)).ToArray());
    }

    internal string? SetMicrophonePriority(IReadOnlyList<MicrophonePriorityItem> devices)
    {
        if (!_gate.Wait(0)) return "Please wait until dictation is ready.";
        try
        {
            if (_audio.IsRecording) return "Finish the current recording before changing microphones.";
            var selected = devices.DistinctBy(item => item.Id).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(MicrophonePath)!);
            var pending = MicrophonePath + ".tmp";
            File.WriteAllText(pending, System.Text.Json.JsonSerializer.Serialize(selected));
            File.Move(pending, MicrophonePath, true);
            _microphones = selected;
            _audio.SetMicrophonePriorityList(selected);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Could not apply microphone: " + ex.Message; }
        finally { _gate.Release(); }
    }

    internal LocalDictationSession(IHistoryService history, IntPtr owner)
    {
        _history = history;
        _inserter = new(owner);
        _effects = new(_ducking, new MediaPauseService());
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _silenceTimer = dispatcher.CreateTimer();
        _silenceTimer.Interval = TimeSpan.FromMilliseconds(100);
        _silenceTimer.Tick += async (_, _) =>
        {
            if (_disposed || !_audio.IsRecording) { StopSilenceMonitoring(); return; }
            if (_silence?.ShouldStop(_silenceClock.Elapsed, ModifiersHeld()) == true)
                await StopAsync();
        };
        _audio.AudioLevelChanged += (_, level) => _silence?.Observe(_silenceClock.Elapsed, level.RmsLevel);
        _audio.DeviceLost += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _audio.IsRecording) return;
            StopSilenceMonitoring();
            _livePreview.Cancel();
            _effects.End();
            if (_phase == DictationPhase.Recording) SetStatus("Microphone disconnected · recording stopped", DictationPhase.Error);
        });
        try
        {
            if (File.Exists(AudioPreferencesPath))
                AudioPreferences = (System.Text.Json.JsonSerializer.Deserialize<DictationAudioPreferences>(File.ReadAllText(AudioPreferencesPath)) ?? new()).Validated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { AudioPreferencesError = "Audio preferences could not be loaded. Defaults are in use: " + ex.Message; }
    }

    internal async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(MicrophonePath))
            {
                var json = File.ReadAllText(MicrophonePath);
                using var document = System.Text.Json.JsonDocument.Parse(json);
                // Preserve the previous single-device preference on upgrade.
                _microphones = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? System.Text.Json.JsonSerializer.Deserialize<List<MicrophonePriorityItem>>(json) ?? []
                    : System.Text.Json.JsonSerializer.Deserialize<MicrophonePriorityItem>(json) is { } previous ? [previous] : [];
                _audio.SetMicrophonePriorityList(_microphones);
            }
            var modelDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeWhisper-DevUserData", "PluginData", "com.typewhisper.sherpa-onnx", "Models", "parakeet-tdt-0.6b");
            foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
                if (!File.Exists(Path.Combine(modelDirectory, name))) throw new FileNotFoundException("Parakeet model is not installed. Model setup is required.");
            _recognizer = await Task.Run(() =>
            {
                var config = new OfflineRecognizerConfig();
                config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
                config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
                config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx");
                config.ModelConfig.Tokens = Path.Combine(modelDirectory, "tokens.txt");
                config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
                config.ModelConfig.Provider = "cpu";
                config.DecodingMethod = "greedy_search";
                return new OfflineRecognizer(config);
            });
            // Prepare the device without starting capture, so key-down need not initialize it.
            var prepared = _audio.WarmUp();
            SetStatus(prepared ? $"Parakeet ready · {Shortcut} to dictate" : "Parakeet ready · microphone preparation failed; check the device");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus("Parakeet unavailable: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    internal Task ToggleAsync() => SetRecordingAsync(null);
    internal Task StartAsync() => SetRecordingAsync(true);
    internal Task StopAsync() => SetRecordingAsync(false);
    internal async Task CancelAsync()
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            StopSilenceMonitoring();
            _livePreview.Cancel();
            if (_audio.IsRecording) await _audio.StopRecordingAsync();
            _effects.End();
            await _livePreview.StopAsync();
            SetStatus("Shortcut cancelled · Parakeet ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("Could not cancel recording: " + ex.Message); }
        finally { _effects.End(); _gate.Release(); }
    }

    private async Task SetRecordingAsync(bool? recording)
    {
        if (_disposed || !await _gate.WaitAsync(0)) return;
        try
        {
            if (recording.HasValue && recording.Value == _audio.IsRecording) return;
            if (_recognizer is null) { SetStatus("Parakeet is not ready. Wait for model loading or check setup."); return; }
            if (!_audio.IsRecording)
            {
                _target = GetForegroundWindow();
                GetWindowThreadProcessId(_target, out var processId);
                if (_target == IntPtr.Zero || processId == Environment.ProcessId)
                {
                    SetStatus($"Focus a text field in another app, then press {Shortcut}.");
                    return;
                }
                var preferences = AudioPreferences;
                await _livePreview.StopAsync();
                if (_disposed) return;
                _audio.WhisperModeEnabled = preferences.WhisperModeEnabled;
                _audio.StartRecording(enableRecovery: false);
                if (!_audio.IsRecording) { SetStatus("Microphone could not start. Check the input device and microphone access."); return; }
                LivePreviewText = "";
                if (LivePreviewEnabled)
                    _livePreview.Start(() => _audio.HasSpeechEnergy ? _audio.GetCurrentBuffer() : null,
                        DecodeAsync,
                        text => { LivePreviewText = text; LivePreviewChanged?.Invoke(); },
                        error => { LivePreviewText = "Live preview unavailable · final transcription will continue."; LivePreviewChanged?.Invoke(); System.Diagnostics.Debug.WriteLine(error); });
                if (preferences.SilenceAutoStopEnabled)
                {
                    _silenceClock.Restart();
                    _silence = new(TimeSpan.FromSeconds(preferences.SilenceAutoStopSeconds));
                    _silenceTimer.Start();
                }
                _sounds.IsEnabled = preferences.SoundFeedbackEnabled;
                _sounds.OutputDeviceId = preferences.OutputDeviceId;
                _ducking.OutputDeviceId = preferences.OutputDeviceId;
                _effects.Begin(preferences);
                _sounds.PlayStartSound();
                _started = DateTime.UtcNow;
                _lastDuration = TimeSpan.Zero;
                _targetProcessId = processId;
                try { using var process = System.Diagnostics.Process.GetProcessById((int)processId); _targetApp = process.ProcessName; }
                catch (ArgumentException) { _targetApp = "Target app"; }
                SetStatus($"Recording · {Shortcut} to finish");
                return;
            }

            StopSilenceMonitoring();
            _livePreview.Cancel();
            _lastDuration = _audio.RecordingDuration;
            SetStatus("Finishing recording…", DictationPhase.Processing);
            var samples = await _audio.StopRecordingAsync();
            _effects.End();
            _sounds.PlayStopSound();
            // Native decoding cannot be interrupted; drain the cancelled preview
            // before the final decode uses the same recognizer.
            await _livePreview.StopAsync();
            if (samples is null || samples.Length < 1600) { SetStatus("No usable audio captured. Try again."); return; }
            SetStatus("Transcribing with Parakeet…", DictationPhase.Processing);
            var text = await DecodeAsync(samples);
            if (string.IsNullOrWhiteSpace(text)) { SetStatus("No speech recognized. Ready to try again."); return; }
            var record = new TranscriptionRecord
            {
                Id = Guid.NewGuid().ToString(), Timestamp = _started, CreatedAt = DateTime.UtcNow,
                RawText = text, FinalText = text, DurationSeconds = samples.Length / 16000.0,
                EngineUsed = "sherpa-onnx", ModelUsed = "parakeet-tdt-0.6b", TranscriptionTaskUsed = "transcribe"
            };
            await _history.EnsureLoadedAsync();
            if (!_history.TryAddRecord(record))
            {
                LastUnsavedText = text;
                SetStatus("History could not be saved. Result retained in memory; no text inserted.");
                return;
            }
            // Never change applications or send Enter automatically. If focus
            // moved during decoding, keep the result in History for manual use.
            for (var attempt = 0; attempt < 40 && ModifiersHeld(); attempt++) await Task.Delay(25);
            if (ModifiersHeld() || GetForegroundWindow() != _target)
            {
                SetStatus("Saved to History · target changed, text was not inserted");
                return;
            }
            try
            {
                var inserted = await _inserter.InsertAsync(text, _target);
                SetStatus(inserted ? "Paste sent and saved · Parakeet ready" : "Saved to History · paste not completed; copy the result manually");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetStatus("Saved to History · clipboard operation failed; check the target and clipboard: " + ex.Message, DictationPhase.Error);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopSilenceMonitoring();
            _livePreview.Cancel();
            try { if (_audio.IsRecording) await _audio.StopRecordingAsync(); }
            catch (Exception stopError) when (stopError is not OutOfMemoryException)
            { System.Diagnostics.Debug.WriteLine(stopError); }
            finally { _effects.End(); await _livePreview.StopAsync(); }
            _sounds.PlayErrorSound();
            SetStatus("Dictation failed: " + ex.Message, DictationPhase.Error);
        }
        finally { if (!_audio.IsRecording) _effects.End(); _gate.Release(); }
    }

    internal string? LastUnsavedText { get; private set; }
    private Task<string> DecodeAsync(float[] samples) => Task.Run(() =>
    {
        using var stream = _recognizer!.CreateStream();
        stream.AcceptWaveform(16000, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    });
    private void StopSilenceMonitoring()
    {
        _silence = null;
        _silenceTimer.Stop();
        _silenceClock.Stop();
    }
    private void SetStatus(string status, DictationPhase? phase = null)
    {
        Status = status;
        _phase = phase ?? (_audio.IsRecording ? DictationPhase.Recording : DictationPhase.Idle);
        Changed?.Invoke();
    }

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose()
    {
        _disposed = true;
        _livePreview.Dispose();
        StopSilenceMonitoring();
        _effects.End();
        _audio.Dispose();
        _inserter.Dispose();
        // Decode cannot be interrupted safely; process shutdown releases it if busy.
        // The preview may still be inside native inference. Drain it before disposing.
        _ = DisposeRecognizerAsync();
    }

    private async Task DisposeRecognizerAsync()
    {
        await _livePreview.StopAsync();
        await _gate.WaitAsync();
        try { _recognizer?.Dispose(); _recognizer = null; }
        finally { _gate.Release(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
