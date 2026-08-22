using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

internal sealed record StreamingModelPreparation(
    string RequestedModelId,
    string? ActiveModelId,
    ITranscriptionEngine? Engine,
    ITranscriptionEnginePlugin? Plugin,
    bool IsReady);

/// <summary>
/// Provides live transcription during recording. Uses real-time WebSocket streaming
/// when the plugin supports it, otherwise falls back to polling-based transcription.
/// </summary>
public sealed class StreamingHandler : IDisposable
{
    private readonly ModelManagerService _modelManager;
    private readonly IStreamingAudioSource _audio;
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
    private const int SampleRate = 16000;
    private const int OnlineBatchPollingWindowSeconds = 30;
    private const int MaximumRollingWindowLeadingTokensToSkip = 12;
    private const int MaximumRollingWindowTrailingTokensToReplace = 12;
    private const int MinimumRollingWindowOverlapWords = 3;
    private static readonly TimeSpan LocalPollingInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OnlineBatchPollingInterval = TimeSpan.FromSeconds(5);
    private const string RollingWindowCjkCharacterClass =
        @"\u1100-\u11FF\u2E80-\u2FFF\u3040-\u30FF\u3100-\u318F\u31A0-\u31BF" +
        @"\u31F0-\u31FF\u3400-\u4DBF\u4E00-\u9FFF\uA960-\uA97F\uAC00-\uD7AF" +
        @"\uD7B0-\uD7FF\uF900-\uFAFF\uFF66-\uFF9D";
    private static readonly Regex RollingWindowWordRegex = new(
        $@"[{RollingWindowCjkCharacterClass}]|" +
        $@"[\p{{L}}\p{{N}}-[{RollingWindowCjkCharacterClass}]]+" +
        $@"(?:['’][\p{{L}}\p{{N}}-[{RollingWindowCjkCharacterClass}]]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Gets or sets the on partial text update value.
    /// </summary>
    public Action<string>? OnPartialTextUpdate { get; set; }

    /// <summary>
    /// Initializes a new instance of the StreamingHandler class.
    /// </summary>
    public StreamingHandler(
        ModelManagerService modelManager,
        IStreamingAudioSource audio,
        IDictionaryService dictionary)
    {
        _modelManager = modelManager;
        _audio = audio;
        _dictionary = dictionary;
    }

    /// <summary>
    /// Starts the service or session.
    /// </summary>
    public void Start(
        string? language,
        TranscriptionTask task,
        Func<bool> isStillRecording) =>
        StartWithLanguageHints(
            string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? []
                : [language],
            task,
            isStillRecording);

    /// <summary>
    /// Starts the service or session with ordered language hints.
    /// </summary>
    public void StartWithLanguageHints(
        IReadOnlyList<string> languageHints,
        TranscriptionTask task,
        Func<bool> isStillRecording)
    {
        Stop();
        ClearPendingStreamingAudio();

        var sessionVersion = _transcriptState.StartSession();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var plugin = _modelManager.ActiveTranscriptionPlugin;
        var prompt = TranscriptionDictionaryPrompt.Create(_dictionary, plugin);

        if (plugin is not null && plugin.SupportsStreamingForPrompt(prompt))
        {
            _audio.SamplesAvailable += OnStreamingSamplesAvailable;
            _streamingTask = RunWebSocketStreamingAsync(plugin, languageHints, sessionVersion, ct);
        }
        else
        {
            _streamingTask = RunPollingFallbackAsync(
                languageHints,
                task,
                isStillRecording,
                sessionVersion,
                ct,
                useOnlineBatchWindow: plugin is { SupportsModelDownload: false });
        }
    }

    /// <summary>
    /// Starts buffering live-preview audio immediately and opens the provider path
    /// after the requested model is ready.
    /// </summary>
    internal void StartWhenReadyWithLanguageHints(
        IReadOnlyList<string> languageHints,
        TranscriptionTask task,
        Func<bool> isStillRecording,
        Task<StreamingModelPreparation> modelPreparation,
        bool allowOnlineBatchPolling)
    {
        Stop();
        ClearPendingStreamingAudio();

        var sessionVersion = _transcriptState.StartSession();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Subscribe before capture starts so a slow model or provider cannot lose
        // the beginning of a real-time preview session.
        _audio.SamplesAvailable += OnStreamingSamplesAvailable;
        _streamingTask = RunWhenReadyAsync(
            languageHints,
            task,
            isStillRecording,
            modelPreparation,
            allowOnlineBatchPolling,
            sessionVersion,
            ct);
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
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

    private async Task RunWhenReadyAsync(
        IReadOnlyList<string> languageHints,
        TranscriptionTask task,
        Func<bool> isStillRecording,
        Task<StreamingModelPreparation> modelPreparation,
        bool allowOnlineBatchPolling,
        int sessionVersion,
        CancellationToken ct)
    {
        try
        {
            var preparation = await modelPreparation.WaitAsync(ct);
            if (!IsCurrentPreparation(preparation)
                || !_transcriptState.IsCurrentSession(sessionVersion)
                || ct.IsCancellationRequested)
            {
                StopPreviewBuffering(sessionVersion);
                return;
            }

            var plugin = preparation.Plugin!;
            var prompt = TranscriptionDictionaryPrompt.Create(_dictionary, plugin);

            if (plugin.SupportsStreamingForPrompt(prompt))
            {
                await RunWebSocketStreamingAsync(plugin, languageHints, sessionVersion, ct);
                return;
            }

            // Polling reads the recorder's complete growing buffer directly. The
            // bounded preview-only packet queue is unnecessary on this path.
            StopPreviewBuffering(sessionVersion);
            if (plugin.SupportsModelDownload || allowOnlineBatchPolling)
            {
                await RunPollingFallbackAsync(
                    languageHints,
                    task,
                    isStillRecording,
                    sessionVersion,
                    ct,
                    preparation.Engine,
                    useOnlineBatchWindow: !plugin.SupportsModelDownload);
            }
        }
        catch (OperationCanceledException)
        {
            StopPreviewBuffering(sessionVersion);
        }
        catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
        {
            Debug.WriteLine($"Delayed streaming startup error: {ex.Message}");
            StopPreviewBuffering(sessionVersion);
        }
    }

    private bool IsCurrentPreparation(StreamingModelPreparation preparation) =>
        preparation.IsReady
        && preparation.Engine?.IsModelLoaded == true
        && preparation.Plugin is not null
        && string.Equals(
            preparation.RequestedModelId,
            preparation.ActiveModelId,
            StringComparison.Ordinal)
        && ReferenceEquals(_modelManager.ActiveTranscriptionPlugin, preparation.Plugin)
        && string.Equals(
            _modelManager.ActiveModelId,
            preparation.ActiveModelId,
            StringComparison.Ordinal);

    private void StopPreviewBuffering(int sessionVersion)
    {
        if (!_transcriptState.IsCurrentSession(sessionVersion))
            return;

        _audio.SamplesAvailable -= OnStreamingSamplesAvailable;
        ClearPendingStreamingAudio();
    }

    private async Task RunWebSocketStreamingAsync(
        ITranscriptionEnginePlugin plugin,
        IReadOnlyList<string> languageHints,
        int sessionVersion,
        CancellationToken ct)
    {
        try
        {
            var session = await plugin.StartStreamingWithLanguageHintsAsync(languageHints, ct);
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
        IReadOnlyList<string> languageHints, TranscriptionTask task,
        Func<bool> isStillRecording, int sessionVersion, CancellationToken ct,
        ITranscriptionEngine? preparedEngine = null,
        bool useOnlineBatchWindow = false)
    {
        var engine = preparedEngine ?? _modelManager.Engine;
        var pollInterval = GetPollingInterval(useOnlineBatchWindow);

        try
        {
            // Keep the first preview responsive, then use the provider-safe repeat cadence.
            await Task.Delay(LocalPollingInterval, ct);

            while (!ct.IsCancellationRequested && isStillRecording())
            {
                var buffer = _audio.GetCurrentBuffer();
                var bufferDuration = buffer is not null ? buffer.Length / 16000.0 : 0;

                if (buffer is not null && bufferDuration > 0.5
                    && _audio.PeakRmsLevel >= AudioRecordingService.SpeechEnergyThreshold)
                {
                    try
                    {
                        var pollingBuffer = SelectPollingBuffer(buffer, useOnlineBatchWindow);
                        var isRollingSnapshot = pollingBuffer.Length < buffer.Length;
                        var result = await engine.TranscribeWithLanguageHintsAsync(
                            pollingBuffer,
                            languageHints,
                            task,
                            ct);

                        var text = result.NoSpeechProbability is > 0.8f
                            ? ""
                            : result.Text?.Trim() ?? "";

                        if (!string.IsNullOrEmpty(text))
                        {
                            bool applied;
                            string stable;
                            if (useOnlineBatchWindow)
                            {
                                applied = isRollingSnapshot
                                    ? _transcriptState.TryApplyRollingPolling(
                                        sessionVersion,
                                        text,
                                        _dictionary.ApplyCorrections,
                                        out stable)
                                    : _transcriptState.TryApplySnapshotPolling(
                                        sessionVersion,
                                        text,
                                        _dictionary.ApplyCorrections,
                                        out stable);
                            }
                            else
                            {
                                applied = _transcriptState.TryApplyPolling(
                                    sessionVersion,
                                    text,
                                    _dictionary.ApplyCorrections,
                                    out stable);
                            }

                            if (applied)
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

    internal static TimeSpan GetPollingInterval(bool useOnlineBatchWindow) =>
        useOnlineBatchWindow ? OnlineBatchPollingInterval : LocalPollingInterval;

    internal static float[] SelectPollingBuffer(float[] buffer, bool useOnlineBatchWindow)
    {
        if (!useOnlineBatchWindow)
            return buffer;

        var maximumSamples = OnlineBatchPollingWindowSeconds * SampleRate;
        return buffer.Length <= maximumSamples
            ? buffer
            : buffer[^maximumSamples..];
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

    /// <summary>
    /// Appends the new tail from an overlapping rolling transcription window.
    /// </summary>
    internal static string MergeRollingText(string confirmed, string windowText)
    {
        confirmed = confirmed.Trim();
        windowText = windowText.Trim();
        if (string.IsNullOrEmpty(confirmed))
            return windowText;
        if (string.IsNullOrEmpty(windowText))
            return confirmed;
        if (windowText.StartsWith(confirmed, StringComparison.Ordinal))
            return windowText;
        if (confirmed.EndsWith(windowText, StringComparison.Ordinal))
            return confirmed;

        var confirmedWords = TokenizeRollingWindowWords(confirmed);
        var windowWords = TokenizeRollingWindowWords(windowText);
        if (confirmedWords.Count < MinimumRollingWindowOverlapWords
            || windowWords.Count < MinimumRollingWindowOverlapWords)
        {
            return confirmed;
        }

        var bestOverlapLength = 0;
        var bestConfirmedTrailingTokensToReplace = 0;
        var bestWindowTailStart = windowText.Length;
        var maximumWindowStart = Math.Min(
            MaximumRollingWindowLeadingTokensToSkip,
            windowWords.Count - MinimumRollingWindowOverlapWords);
        var maximumConfirmedTrailingTokensToReplace = Math.Min(
            MaximumRollingWindowTrailingTokensToReplace,
            confirmedWords.Count - MinimumRollingWindowOverlapWords);

        for (var confirmedTrailingTokensToReplace = 0;
             confirmedTrailingTokensToReplace <= maximumConfirmedTrailingTokensToReplace;
             confirmedTrailingTokensToReplace++)
        {
            var confirmedEnd = confirmedWords.Count - confirmedTrailingTokensToReplace;
            for (var windowStart = 0; windowStart <= maximumWindowStart; windowStart++)
            {
                var maximumOverlap = Math.Min(confirmedEnd, windowWords.Count - windowStart);
                for (var overlapLength = maximumOverlap;
                     overlapLength >= MinimumRollingWindowOverlapWords;
                     overlapLength--)
                {
                    var confirmedStart = confirmedEnd - overlapLength;
                    if (!RollingWindowWordsEqual(
                            confirmedWords,
                            confirmedStart,
                            windowWords,
                            windowStart,
                            overlapLength))
                    {
                        continue;
                    }

                    if (overlapLength > bestOverlapLength)
                    {
                        bestOverlapLength = overlapLength;
                        bestConfirmedTrailingTokensToReplace = confirmedTrailingTokensToReplace;
                        bestWindowTailStart = windowWords[windowStart + overlapLength - 1].End;
                    }

                    break;
                }
            }
        }

        if (bestOverlapLength == 0)
            return confirmed;

        var tail = windowText[bestWindowTailStart..];
        if (string.IsNullOrEmpty(tail))
            return confirmed;

        var stablePrefix = bestConfirmedTrailingTokensToReplace == 0
            ? confirmed
            : confirmed[..confirmedWords[^bestConfirmedTrailingTokensToReplace].Start].TrimEnd();
        return AppendRollingTail(stablePrefix, tail);
    }

    private static string AppendRollingTail(string prefix, string tail)
    {
        prefix = prefix.TrimEnd();
        tail = tail.TrimStart();
        if (string.IsNullOrEmpty(prefix))
            return tail;
        if (string.IsNullOrEmpty(tail))
            return prefix;

        var firstWordCharacter = 0;
        while (firstWordCharacter < tail.Length
               && !char.IsLetterOrDigit(tail[firstWordCharacter]))
        {
            firstWordCharacter++;
        }

        var boundary = tail[..firstWordCharacter];
        var remainingTail = tail[firstWordCharacter..].TrimStart();
        var punctuationLength = boundary.Length;
        while (punctuationLength > 0 && char.IsWhiteSpace(boundary[punctuationLength - 1]))
            punctuationLength--;

        var punctuation = boundary[..punctuationLength];
        var boundaryToAppend = !string.IsNullOrEmpty(punctuation)
            && (prefix.EndsWith(punctuation, StringComparison.Ordinal)
                || HasRollingBoundaryPunctuation(prefix[^1]))
                ? boundary[punctuationLength..]
                : boundary;
        if (string.IsNullOrEmpty(boundaryToAppend) && string.IsNullOrEmpty(boundary))
        {
            boundaryToAppend = " ";
        }

        return prefix + boundaryToAppend + remainingTail;
    }

    private static bool HasRollingBoundaryPunctuation(char value) =>
        ".,!?;:…。！？、，；：".Contains(value);

    private static List<RollingWindowWord> TokenizeRollingWindowWords(string text) =>
        RollingWindowWordRegex.Matches(text)
            .Select(match => new RollingWindowWord(
                match.Value.Replace('’', '\'').ToUpperInvariant(),
                match.Index,
                match.Index + match.Length))
            .ToList();

    private static bool RollingWindowWordsEqual(
        IReadOnlyList<RollingWindowWord> first,
        int firstStart,
        IReadOnlyList<RollingWindowWord> second,
        int secondStart,
        int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (!string.Equals(
                    first[firstStart + i].Normalized,
                    second[secondStart + i].Normalized,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record RollingWindowWord(string Normalized, int Start, int End);

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        Stop();
    }
}
