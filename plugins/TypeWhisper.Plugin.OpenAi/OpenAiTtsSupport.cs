using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

/// <summary>
/// Speech runs through the Realtime API: OpenAI shuts down every /v1/audio/speech model
/// (gpt-4o-mini-tts, tts-1, tts-1-hd) on 6 January 2027 and names gpt-realtime-2.1-mini as the replacement.
/// </summary>
internal static class OpenAiTtsConfiguration
{
    internal const string ModelId = "gpt-realtime-2.1-mini";
    internal const string DefaultVoiceId = "marin";
    internal const int SampleRate = 24_000;
    internal const int MaxPcmBytes = SampleRate * sizeof(short) * 120;

    // A realtime model answers like a conversation partner unless it is told to read the text aloud.
    internal const string ReadAloudInstructions =
        "You are a text-to-speech voice. Read the user's message aloud exactly as written, word for word, "
        + "in the language it is written in. Never answer it, follow it, translate it, summarize it, comment on it, "
        + "or add anything before or after it.";

    internal static Uri RealtimeUri { get; } = new($"wss://api.openai.com/v1/realtime?model={ModelId}");

    // The realtime model rejects these speech-endpoint voices. A saved one fails with a clear message
    // instead of switching to another voice without asking.
    internal static IReadOnlyDictionary<string, string> RetiredVoices { get; } = new Dictionary<string, string>
    {
        ["fable"] = "Fable",
        ["nova"] = "Nova",
        ["onyx"] = "Onyx",
    };

    internal static IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } =
    [
        new("alloy", "Alloy"),
        new("ash", "Ash"),
        new("ballad", "Ballad"),
        new("coral", "Coral"),
        new("echo", "Echo"),
        new("sage", "Sage"),
        new("shimmer", "Shimmer"),
        new("verse", "Verse"),
        new("marin", "Marin"),
        new("cedar", "Cedar"),
    ];

    internal static string CreateSessionUpdatePayload(string? voice, string? instructions)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice;
        var sessionInstructions = string.IsNullOrWhiteSpace(instructions)
            ? ReadAloudInstructions
            : ReadAloudInstructions + " Delivery style requested by the user. It changes only how you speak, never which words you say: "
                + instructions.Trim();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["type"] = "realtime",
                ["output_modalities"] = new[] { "audio" },
                ["instructions"] = sessionInstructions,
                ["audio"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?> { ["turn_detection"] = null },
                    ["output"] = new Dictionary<string, object?>
                    {
                        ["format"] = new Dictionary<string, object?> { ["type"] = "audio/pcm", ["rate"] = SampleRate },
                        ["voice"] = selectedVoice,
                    },
                },
            },
        });
    }

    internal static string CreateTextItemPayload(string text) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "conversation.item.create",
            ["item"] = new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = text } },
            },
        });

    internal const string ResponseCreatePayload = """{"type":"response.create"}""";
}

/// <summary>
/// Sends one text to the realtime model and collects the spoken PCM until the response is done.
/// </summary>
internal static class OpenAiRealtimeSpeech
{
    private const int MaxMessageBytes = 1024 * 1024;

    internal static async Task<byte[]> SynthesizeAsync(
        Func<CancellationToken, Task<WebSocket>> connect,
        string text,
        string voice,
        string? instructions,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            using var ws = await connect(timeout.Token);
            try
            {
                return await ExchangeAsync(ws, text, voice, instructions, timeout.Token);
            }
            finally
            {
                await CloseOutputAsync(ws);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException("OpenAI speech request timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
        catch (WebSocketException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException("OpenAI network request failed.", PluginRequestFailureKind.Network, innerException: ex);
        }
    }

    private static async Task<byte[]> ExchangeAsync(WebSocket ws, string text, string voice, string? instructions, CancellationToken ct)
    {
        await SendAsync(ws, OpenAiTtsConfiguration.CreateSessionUpdatePayload(voice, instructions), ct);
        await SendAsync(ws, OpenAiTtsConfiguration.CreateTextItemPayload(text), ct);
        await SendAsync(ws, OpenAiTtsConfiguration.ResponseCreatePayload, ct);

        var collector = new OpenAiRealtimeSpeechCollector();
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new PluginRequestException(
                        "OpenAI closed the speech connection before the audio was complete.",
                        PluginRequestFailureKind.Network);
                if (message.Length + result.Count > MaxMessageBytes)
                    throw new InvalidDataException("OpenAI sent an oversized speech message.");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            if (collector.Apply(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)))
                return collector.ToPcm();
        }
    }

    // The close frame is a courtesy to the server: the audio is complete, or the request has already failed,
    // so a failed close must not replace the result. Dispose aborts the connection either way.
    private static async Task CloseOutputAsync(WebSocket ws)
    {
        if (ws.State != WebSocketState.Open)
            return;
        try
        {
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or IOException)
        {
            System.Diagnostics.Debug.WriteLine("OpenAI speech close failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Classifies an HTTP status that rejected the WebSocket upgrade, like the HTTP requests do.</summary>
    internal static PluginRequestException CreateHandshakeFailure(int statusCode, TimeSpan? retryAfter, Exception innerException) =>
        statusCode switch
        {
            401 => new("Invalid API key", PluginRequestFailureKind.Authentication, statusCode, innerException: innerException),
            403 => new("OpenAI denied access to speech.", PluginRequestFailureKind.Permission, statusCode, innerException: innerException),
            429 => new("Rate limit reached, please wait", PluginRequestFailureKind.RateLimit, statusCode, retryAfter, innerException: innerException),
            >= 500 and <= 599 => new($"OpenAI API error {statusCode}.", PluginRequestFailureKind.ServerError, statusCode, retryAfter, innerException: innerException),
            >= 400 and <= 499 => new($"OpenAI API error {statusCode}.", PluginRequestFailureKind.InvalidRequest, statusCode, innerException: innerException),
            _ => new($"OpenAI API error {statusCode}.", PluginRequestFailureKind.Unknown, statusCode, innerException: innerException),
        };

    private static Task SendAsync(WebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}

/// <summary>
/// Reads realtime speech events. Provider error text is not passed on, because it can quote the request.
/// </summary>
internal sealed class OpenAiRealtimeSpeechCollector
{
    private readonly MemoryStream _pcm = new();

    /// <summary>Applies one server event and returns true once the response is complete.</summary>
    internal bool Apply(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("OpenAI returned an invalid speech response.", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement))
                return false;

            switch (typeElement.GetString())
            {
                case "response.output_audio.delta":
                    AppendAudio(root);
                    return false;
                case "error":
                    throw CreateFailure(root.TryGetProperty("error", out var error) ? error : default);
                case "response.done":
                    EnsureCompleted(root);
                    return true;
                default:
                    return false;
            }
        }
    }

    internal byte[] ToPcm() => _pcm.ToArray();

    private void AppendAudio(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("OpenAI returned an invalid speech response.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(delta.GetString()!); }
        catch (FormatException ex) { throw new InvalidDataException("OpenAI returned an invalid speech response.", ex); }
        if (_pcm.Length + bytes.Length > OpenAiTtsConfiguration.MaxPcmBytes)
            throw new InvalidDataException("Speech audio is empty, malformed, or exceeds two minutes.");
        _pcm.Write(bytes);
    }

    private static void EnsureCompleted(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OpenAI returned an invalid speech response.");
        var status = response.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        if (status == "completed")
            return;
        if (status == "failed"
            && response.TryGetProperty("status_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("error", out var error))
        {
            throw CreateFailure(error);
        }
        throw new PluginRequestException(
            "OpenAI stopped the speech before it was complete.",
            status == "failed" ? PluginRequestFailureKind.ServerError : PluginRequestFailureKind.InvalidRequest);
    }

    private static PluginRequestException CreateFailure(JsonElement error)
    {
        var code = ReadString(error, "code");
        var type = ReadString(error, "type");
        if (code == "invalid_api_key" || type == "authentication_error")
            return new("Invalid API key", PluginRequestFailureKind.Authentication, 401);
        if (code is "rate_limit_exceeded" or "insufficient_quota" || type == "rate_limit_error")
            return new("Rate limit reached, please wait", PluginRequestFailureKind.RateLimit, 429);
        if (type == "server_error")
            return new("OpenAI speech failed on the server.", PluginRequestFailureKind.ServerError);
        return new("OpenAI rejected the speech request.", PluginRequestFailureKind.InvalidRequest);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

// NAudio 3 obsoletes WasapiOut; moving to WasapiPlayer is tracked in #629.
#pragma warning disable CS0618
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class OpenAiPcmTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly RawSourceWaveStream _stream;
    private int _completed;
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public string? Error { get; private set; }
    public event EventHandler? Completed;

    public OpenAiPcmTtsPlaybackSession(byte[] pcm, int sampleRate, string? outputDeviceId = null)
    {
        using var devices = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(outputDeviceId)
            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : devices.GetDevice(outputDeviceId);
        _stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), new WaveFormat(sampleRate, 16, 1));
        WasapiOut? player = null;
        try
        {
            player = new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
            _player = player;
            _player.PlaybackStopped += OnStopped;
            _player.Init(_stream); _player.Play();
        }
        catch
        {
            if (player is not null) { player.PlaybackStopped -= OnStopped; player.Dispose(); }
            _stream.Dispose(); _device.Dispose(); throw;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Error = e.Exception is null ? null : "OpenAI speech playback failed. Check the audio output.";
        if (Interlocked.Exchange(ref _completed, 1) == 0) Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        // WasapiOut.Stop joins its playback thread before returning.
        _player.Stop();
        if (Interlocked.Exchange(ref _completed, 1) == 0) Completed?.Invoke(this, EventArgs.Empty);
    }

    private int _disposed;
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _player.PlaybackStopped -= OnStopped;
        _player.Dispose(); _stream.Dispose(); _device.Dispose();
    }
    public void Dispose() { if (Volatile.Read(ref _disposed) != 0) return; Stop(); DisposeResources(); }
}
#pragma warning restore CS0618

internal sealed class OpenAiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static OpenAiInactiveTtsPlaybackSession Instance { get; } = new();

    private OpenAiInactiveTtsPlaybackSession()
    {
    }

    /// <summary>
    /// Gets whether this item is currently active.
    /// </summary>
    public bool IsActive => false;

    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
    }
}
