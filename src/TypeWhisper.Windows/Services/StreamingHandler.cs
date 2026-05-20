using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
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
    private readonly StreamingTranscriptState _transcriptState = new();
    private readonly object _streamingAudioLock = new();
    private readonly Queue<byte[]> _pendingStreamingAudio = new();

    private CancellationTokenSource? _cts;
    private Task? _streamingTask;
    private Task? _streamingAudioSenderTask;
    private IStreamingSession? _session;
    private ChannelWriter<byte[]>? _streamingAudioWriter;
    private Action<StreamingTranscriptEvent>? _transcriptHandler;
    private int _pendingStreamingAudioBytes;
    private bool _isFlushingPendingStreamingAudio;

    private const int MaxPendingStreamingAudioBytes = 1024 * 1024;
    private const int StreamingAudioChannelCapacity = 128;

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
        ClearPendingStreamingAudio();

        var sessionVersion = _transcriptState.StartSession();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var plugin = _modelManager.ActiveTranscriptionPlugin;

        if (plugin is not null && plugin.SupportsStreaming)
        {
            _audio.SamplesAvailable += OnStreamingSamplesAvailable;
            _streamingTask = RunWebSocketStreamingAsync(plugin, language, sessionVersion, ct);
        }
        else
        {
            _streamingTask = RunPollingFallbackAsync(language, task, isStillRecording, sessionVersion, ct);
        }
    }

    public string Stop()
    {
        _audio.SamplesAvailable -= OnStreamingSamplesAvailable;
        _cts?.Cancel();

        var finalText = _transcriptState.StopSession();

        IStreamingSession? session;
        ChannelWriter<byte[]>? audioWriter;
        Action<StreamingTranscriptEvent>? transcriptHandler;
        lock (_streamingAudioLock)
        {
            session = _session;
            audioWriter = _streamingAudioWriter;
            transcriptHandler = _transcriptHandler;
            _session = null;
            _streamingAudioWriter = null;
            _streamingAudioSenderTask = null;
            _transcriptHandler = null;
            ClearPendingStreamingAudioCore();
        }
        audioWriter?.TryComplete();

        if (session is not null && transcriptHandler is not null)
            session.TranscriptReceived -= transcriptHandler;

        if (session is not null)
        {
            // Fire-and-forget with timeout to avoid deadlock
            _ = CleanupSessionAsync(session);
        }

        _cts?.Dispose();
        _cts = null;
        _streamingTask = null;

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
        ITranscriptionEnginePlugin plugin, string? language, int sessionVersion, CancellationToken ct)
    {
        try
        {
            var lang = language == "auto" ? null : language;
            var session = await plugin.StartStreamingAsync(lang, ct);
            if (!_transcriptState.IsCurrentSession(sessionVersion) || ct.IsCancellationRequested)
            {
                await CleanupSessionAsync(session);
                return;
            }

            var audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(StreamingAudioChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

            lock (_streamingAudioLock)
            {
                _session = session;
                _streamingAudioWriter = audioChannel.Writer;
                _isFlushingPendingStreamingAudio = true;
            }

            _transcriptHandler = evt => OnTranscriptReceived(evt, sessionVersion);
            session.TranscriptReceived += _transcriptHandler;
            _streamingAudioSenderTask = RunStreamingAudioSenderAsync(session, audioChannel.Reader, ct);
            await FlushPendingStreamingAudioAsync(audioChannel.Writer, ct);

            // Keep alive until cancelled
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"WebSocket streaming error: {ex.Message}");
            CleanupStreamingSessionAfterFailure();
        }
    }

    private void OnStreamingSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;

        var pcm16 = FloatToPcm16(e.Samples);
        var audioWriter = GetStreamingAudioWriterOrBuffer(pcm16);
        if (audioWriter is null)
            return;

        SendStreamingAudio(audioWriter, pcm16);
    }

    private void SendStreamingAudio(ChannelWriter<byte[]> audioWriter, byte[] pcm16)
    {
        try
        {
            if (!audioWriter.TryWrite(pcm16))
                Debug.WriteLine("Streaming audio queue rejected a chunk.");
        }
        catch (ChannelClosedException ex)
        {
            Debug.WriteLine($"Streaming audio queue closed: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"Streaming audio queue disposed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Streaming audio queue unavailable: {ex.Message}");
        }
    }

    private ChannelWriter<byte[]>? GetStreamingAudioWriterOrBuffer(byte[] pcm16)
    {
        lock (_streamingAudioLock)
        {
            if (_streamingAudioWriter is null || _isFlushingPendingStreamingAudio)
            {
                EnqueuePendingStreamingAudioCore(pcm16);
                return null;
            }

            return _streamingAudioWriter;
        }
    }

    private void EnqueuePendingStreamingAudioCore(byte[] pcm16)
    {
        _pendingStreamingAudio.Enqueue(pcm16);
        _pendingStreamingAudioBytes += pcm16.Length;

        while (_pendingStreamingAudioBytes > MaxPendingStreamingAudioBytes
            && _pendingStreamingAudio.Count > 0)
        {
            _pendingStreamingAudioBytes -= _pendingStreamingAudio.Dequeue().Length;
        }
    }

    private async Task FlushPendingStreamingAudioAsync(ChannelWriter<byte[]> audioWriter, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[]? next;
            lock (_streamingAudioLock)
            {
                if (_pendingStreamingAudio.Count == 0)
                {
                    _isFlushingPendingStreamingAudio = false;
                    return;
                }

                next = _pendingStreamingAudio.Dequeue();
                _pendingStreamingAudioBytes -= next.Length;
            }

            await audioWriter.WriteAsync(next, ct);
        }
    }

    private void ClearPendingStreamingAudio()
    {
        lock (_streamingAudioLock)
        {
            ClearPendingStreamingAudioCore();
        }
    }

    private void ClearPendingStreamingAudioCore()
    {
        _pendingStreamingAudio.Clear();
        _pendingStreamingAudioBytes = 0;
        _isFlushingPendingStreamingAudio = false;
    }

    private async Task RunStreamingAudioSenderAsync(
        IStreamingSession session,
        ChannelReader<byte[]> audioReader,
        CancellationToken ct)
    {
        try
        {
            await foreach (var pcm16 in audioReader.ReadAllAsync(ct))
            {
                await session.SendAudioAsync(pcm16, ct);
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"Streaming audio sender canceled: {ex.Message}");
        }
        catch (ChannelClosedException ex) { HandleStreamingAudioSenderFailure(session, ex); }
        catch (ObjectDisposedException ex) { HandleStreamingAudioSenderFailure(session, ex); }
        catch (InvalidOperationException ex) { HandleStreamingAudioSenderFailure(session, ex); }
        catch (IOException ex) { HandleStreamingAudioSenderFailure(session, ex); }
        catch (WebSocketException ex) { HandleStreamingAudioSenderFailure(session, ex); }
    }

    private void HandleStreamingAudioSenderFailure(IStreamingSession session, Exception ex)
    {
        Debug.WriteLine($"SendAudio error: {ex.Message}");
        CleanupStreamingSessionAfterFailure(session);
    }

    private void CleanupStreamingSessionAfterFailure(IStreamingSession? failedSession = null)
    {
        IStreamingSession? sessionToCleanup;
        ChannelWriter<byte[]>? audioWriter;
        Action<StreamingTranscriptEvent>? transcriptHandler;

        lock (_streamingAudioLock)
        {
            if (failedSession is not null && !ReferenceEquals(_session, failedSession))
                return;

            sessionToCleanup = _session;
            audioWriter = _streamingAudioWriter;
            transcriptHandler = _transcriptHandler;
            _session = null;
            _streamingAudioWriter = null;
            _streamingAudioSenderTask = null;
            _transcriptHandler = null;
            ClearPendingStreamingAudioCore();
        }

        _audio.SamplesAvailable -= OnStreamingSamplesAvailable;
        audioWriter?.TryComplete();

        if (sessionToCleanup is not null && transcriptHandler is not null)
            sessionToCleanup.TranscriptReceived -= transcriptHandler;

        if (sessionToCleanup is not null)
            _ = CleanupSessionAsync(sessionToCleanup);
    }

    private void OnTranscriptReceived(StreamingTranscriptEvent evt, int sessionVersion)
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;

        if (_transcriptState.TryApplyRealtime(sessionVersion, evt, _dictionary.ApplyCorrections, out var display))
            OnPartialTextUpdate?.Invoke(display);
    }

    // ── Polling fallback path ──

    private async Task RunPollingFallbackAsync(
        string? language, TranscriptionTask task,
        Func<bool> isStillRecording, int sessionVersion, CancellationToken ct)
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
                            if (_transcriptState.TryApplyPolling(
                                sessionVersion,
                                text,
                                _dictionary.ApplyCorrections,
                                out var stable))
                            {
                                OnPartialTextUpdate?.Invoke(stable);
                            }
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
