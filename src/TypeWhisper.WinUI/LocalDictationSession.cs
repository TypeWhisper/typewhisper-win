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
    private readonly IHistoryService _history;
    private readonly ClipboardTextInserter _inserter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private IntPtr _target;
    private DateTime _started;
    private bool _disposed;
    internal string Status { get; private set; } = "Loading Parakeet…";
    internal string Shortcut { get; set; } = "Ctrl+Shift+F9";
    internal bool IsRecording => _audio.IsRecording;
    internal bool IsReady => _recognizer is not null;
    internal float CurrentLevel => _audio.CurrentRmsLevel;
    internal event Action? Changed;

    internal LocalDictationSession(IHistoryService history, IntPtr owner)
    {
        _history = history;
        _inserter = new(owner);
    }

    internal async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
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
            if (_audio.IsRecording) await _audio.StopRecordingAsync();
            SetStatus("Shortcut cancelled · Parakeet ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("Could not cancel recording: " + ex.Message); }
        finally { _gate.Release(); }
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
                _audio.StartRecording(enableRecovery: false);
                if (!_audio.IsRecording) { SetStatus("Microphone could not start. Check the input device and microphone access."); return; }
                _started = DateTime.UtcNow;
                SetStatus($"Recording · {Shortcut} to finish");
                return;
            }

            SetStatus("Finishing recording…");
            var samples = await _audio.StopRecordingAsync();
            if (samples is null || samples.Length < 1600) { SetStatus("No usable audio captured. Try again."); return; }
            SetStatus("Transcribing with Parakeet…");
            var text = await Task.Run(() =>
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples);
                _recognizer.Decode(stream);
                return stream.Result.Text.Trim();
            });
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
                SetStatus("Saved to History · clipboard operation failed; check the target and clipboard: " + ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus("Dictation failed: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    internal string? LastUnsavedText { get; private set; }
    private void SetStatus(string status) { Status = status; Changed?.Invoke(); }

    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose()
    {
        _disposed = true;
        _audio.Dispose();
        _inserter.Dispose();
        // Decode cannot be interrupted safely; process shutdown releases it if busy.
        if (_gate.Wait(0)) { _recognizer?.Dispose(); _recognizer = null; _gate.Release(); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
