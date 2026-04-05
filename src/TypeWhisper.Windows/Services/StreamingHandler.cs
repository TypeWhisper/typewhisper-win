using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides live transcription during recording. Uses real-time WebSocket streaming
/// when the plugin supports it, otherwise falls back to polling-based transcription.
/// </summary>
public sealed class StreamingHandler : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly AudioRecordingService _audio;
    private readonly IDictionaryService _dictionary;

    private CancellationTokenSource? _cts;
    private Task? _streamingTask;
    private IStreamingSession? _session;
    private string _confirmedText = "";
    private string _lastDisplayedText = "";

    public Action<string>? OnPartialTextUpdate { get; set; }

    public StreamingHandler(
        ModelManagerService modelManager,
        AudioRecordingService audio,
        IDictionaryService dictionary)
    {
        _modelManager = modelManager;
        _audio = audio;
        _dictionary = dictionary;
    }

    public void Start(
        string? language,
        TranscriptionTask task,
        Func<bool> isStillRecording)
    {
        Stop();

        _confirmedText = "";
        _lastDisplayedText = "";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var plugin = _modelManager.ActiveTranscriptionPlugin;

        if (plugin is not null && plugin.SupportsStreaming)
            _streamingTask = RunWebSocketStreamingAsync(plugin, language, ct);
        else
            _streamingTask = RunPollingFallbackAsync(language, task, isStillRecording, ct);
    }

    public string Stop()
    {
        _audio.SamplesAvailable -= OnStreamingSamplesAvailable;
        _cts?.Cancel();

        var finalText = _lastDisplayedText;

        var session = _session;
        _session = null;
        if (session is not null)
        {
            // Fire-and-forget with timeout to avoid deadlock
            _ = CleanupSessionAsync(session);
        }

        _cts?.Dispose();
        _cts = null;
        _streamingTask = null;
        _confirmedText = "";
        _lastDisplayedText = "";

        return finalText;
    }

    private static async Task CleanupSessionAsync(IStreamingSession session)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await session.FinalizeAsync(timeoutCts.Token); }
        catch { /* best effort */ }
        try { await session.DisposeAsync(); }
        catch { /* best effort */ }
    }

    // ── WebSocket streaming path ──

    private async Task RunWebSocketStreamingAsync(
        ITranscriptionEnginePlugin plugin, string? language, CancellationToken ct)
    {
        try
        {
            var lang = language == "auto" ? null : language;
            _session = await plugin.StartStreamingAsync(lang, ct);

            _session.TranscriptReceived += OnTranscriptReceived;
            _audio.SamplesAvailable += OnStreamingSamplesAvailable;

            // Keep alive until cancelled
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebSocket streaming error: {ex.Message}");
        }
    }

    private void OnStreamingSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        var session = _session;
        var cts = _cts;
        if (session is null || cts is null || cts.IsCancellationRequested) return;

        var pcm16 = FloatToPcm16(e.Samples);
        _ = Task.Run(async () =>
        {
            try { await session.SendAudioAsync(pcm16, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"SendAudio error: {ex.Message}"); }
        });
    }

    private void OnTranscriptReceived(StreamingTranscriptEvent evt)
    {
        var text = evt.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        text = _dictionary.ApplyCorrections(text);

        if (evt.IsFinal)
        {
            _confirmedText = string.IsNullOrEmpty(_confirmedText)
                ? text
                : _confirmedText + " " + text;
            _lastDisplayedText = _confirmedText;
            OnPartialTextUpdate?.Invoke(_confirmedText);
        }
        else
        {
            var display = string.IsNullOrEmpty(_confirmedText)
                ? text
                : _confirmedText + " " + text;
            _lastDisplayedText = display;
            OnPartialTextUpdate?.Invoke(display);
        }
    }

    // ── Polling fallback path ──

    private async Task RunPollingFallbackAsync(
        string? language, TranscriptionTask task,
        Func<bool> isStillRecording, CancellationToken ct)
    {
        var engine = _modelManager.Engine;
        var pollInterval = TimeSpan.FromSeconds(3.0);

        try
        {
            await Task.Delay(pollInterval, ct);

            while (!ct.IsCancellationRequested && isStillRecording())
            {
                var buffer = _audio.GetCurrentBuffer();
                var bufferDuration = buffer is not null ? buffer.Length / 16000.0 : 0;

                if (buffer is not null && bufferDuration > 0.5
                    && _audio.PeakRmsLevel >= AudioRecordingService.SpeechEnergyThreshold)
                {
                    try
                    {
                        var lang = language == "auto" ? null : language;
                        var result = await engine.TranscribeAsync(buffer, lang, task, ct);

                        if (result.NoSpeechProbability is > 0.8f)
                            continue;

                        var text = result.Text?.Trim() ?? "";

                        if (!string.IsNullOrEmpty(text))
                        {
                            text = _dictionary.ApplyCorrections(text);
                            var stable = StabilizeText(_confirmedText, text);
                            _confirmedText = stable;
                            OnPartialTextUpdate?.Invoke(stable);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Streaming transcription error (non-fatal): {ex.Message}");
                    }
                }

                await Task.Delay(pollInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Helpers ──

    /// <summary>Converts float[-1..1] PCM samples to 16-bit signed little-endian bytes.</summary>
    internal static byte[] FloatToPcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)(clamped * 32767f);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// Keeps confirmed text stable and only appends new content.
    /// Used only in polling fallback path.
    /// </summary>
    public static string StabilizeText(string confirmed, string newText)
    {
        newText = newText.Trim();
        if (string.IsNullOrEmpty(confirmed)) return newText;
        if (string.IsNullOrEmpty(newText)) return confirmed;

        if (newText.StartsWith(confirmed, StringComparison.Ordinal))
            return newText;

        var matchEnd = 0;
        var minLen = Math.Min(confirmed.Length, newText.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (confirmed[i] == newText[i])
                matchEnd = i + 1;
            else
                break;
        }

        if (matchEnd > confirmed.Length / 2)
        {
            var tail = newText[matchEnd..];
            if (tail.Length > 0 && !confirmed.EndsWith(' ') && !tail.StartsWith(' '))
                return confirmed + " " + tail;
            return confirmed + tail;
        }

        var minOverlap = Math.Min(20, confirmed.Length / 4);
        var maxShift = Math.Min(confirmed.Length - minOverlap, 150);
        if (maxShift > 0)
        {
            for (var dropCount = 1; dropCount <= maxShift; dropCount++)
            {
                var suffix = confirmed[dropCount..];
                if (newText.StartsWith(suffix, StringComparison.Ordinal))
                {
                    var newTail = newText[(confirmed.Length - dropCount)..];
                    return string.IsNullOrEmpty(newTail) ? confirmed : confirmed + newTail;
                }
            }
        }

        return newText;
    }

    public void Dispose()
    {
        Stop();
    }
}
