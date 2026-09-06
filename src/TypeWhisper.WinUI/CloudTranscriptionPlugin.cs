using System.Text;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.WinUI;

internal sealed record CloudTranscriptionLease(ITranscriptionEnginePlugin Engine, IApiKeyPlugin Configuration, IAsyncDisposable Lifetime);

// Owns package/configuration operations independently of the UI so CI can test them.
internal sealed class CloudTranscriptionPlugin(IPluginHostServices host, Func<Task<CloudTranscriptionLease>> load) : IAsyncDisposable
{
    internal const string PluginId = "com.typewhisper.groq";
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CloudTranscriptionLease? _lease;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    internal event Action? Changed;
    internal bool Enabled => _lease is not null;
    internal bool Ready => _lease?.Configuration.IsConfigured == true;
    internal bool Busy { get; private set; }
    internal string? Error { get; private set; }
    internal string? Feedback { get; private set; }
    internal IReadOnlyList<PluginModelInfo> Models => _lease?.Engine.TranscriptionModels ?? [];
    internal string? ModelId => _lease?.Engine.SelectedModelId;
    internal string ModelName => Models.FirstOrDefault(m => m.Id == ModelId)?.DisplayName ?? "Groq";
    internal IReadOnlyList<string> Languages => _lease?.Engine.SupportedLanguages ?? [];
    internal string Language => host.GetSetting<string>("Language") is { } language && Languages.Contains(language) ? language : "auto";

    internal async Task InitializeAsync()
    {
        if (host.GetSetting<bool?>("Enabled") == true) await SetEnabledAsync(true);
    }

    internal Task SetEnabledAsync(bool enabled) => RunAsync(async () =>
    {
        if (enabled)
        {
            await EnableCoreAsync();
        }
        else
        {
            host.SetSetting("Enabled", false);
            await ReleaseAsync();
        }
    });

    internal Task SaveKeyAsync(string key) => RunAsync(async () =>
    {
        if (!Enabled && !string.IsNullOrWhiteSpace(key)) await EnableCoreAsync();
        await RequireLease().Configuration.SetApiKeyAsync(key);
        Feedback = Ready ? "API key saved. Check connection to verify it." : "API key removed.";
    });

    private async Task EnableCoreAsync()
    {
        if (Enabled) return;
        var lease = await load();
        try { host.SetSetting("Enabled", true); }
        catch { await lease.Lifetime.DisposeAsync(); throw; }
        _lease = lease;
    }

    internal Task ValidateAsync() => RunAsync(async () =>
    {
        await RequireLease().Configuration.ValidateConfigurationAsync(_shutdown.Token);
        Feedback = "Connected to Groq. No audio was uploaded.";
    });

    internal Task SelectModelAsync(string id) => RunAsync(() =>
    {
        RequireLease().Engine.SelectModel(id);
        return Task.CompletedTask;
    });

    internal void SelectLanguage(string language)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Busy || !Enabled) throw new InvalidOperationException("Wait until Groq is ready.");
        if (language != "auto" && !Languages.Contains(language)) throw new ArgumentException("Unsupported language.");
        host.SetSetting("Language", language); Changed?.Invoke();
    }

    internal async Task<(string Text, VocabularyTokenTiming[] Timings)> DecodeAsync(float[] samples)
    {
        (string Text, VocabularyTokenTiming[] Timings) result = ("", []);
        await RunAsync(async () =>
        {
            if (!Ready) throw new InvalidOperationException("Add an API key in Plugins > Groq > Settings.");
            var response = await RequireLease().Engine.TranscribeAsync(EncodeWav(samples),
                Language == "auto" ? null : Language, false, null, _shutdown.Token);
            result = (response.Text, response.TokenTimings.ToArray());
        });
        return result;
    }

    // Mono 16 kHz PCM16 avoids platform codecs and keeps uploads small.
    internal static byte[] EncodeWav(float[] samples)
    {
        if (samples.Length == 0) throw new ArgumentException("No audio captured.");
        if (samples.LongLength * 2 + 44 > 25_000_000)
            throw new PluginRequestException("Recording exceeds Groq's 25 MB upload limit. Use a shorter recording.", PluginRequestFailureKind.RequestTooLarge);
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio contains invalid samples.");
            writer.Write((short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1, 1) * 32768), short.MinValue, short.MaxValue));
        }
        writer.Flush(); return stream.ToArray();
    }

    private CloudTranscriptionLease RequireLease() => _lease ?? throw new InvalidOperationException("Enable Groq in Plugins first.");
    private async Task RunAsync(Func<Task> action)
    {
        if (!await _operations.WaitAsync(0)) throw new InvalidOperationException("A Groq operation is already in progress.");
        Busy = true; Error = null; Feedback = null; Changed?.Invoke();
        try { ObjectDisposedException.ThrowIf(_disposed, this); await action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = DescribeError(ex);
            // Provider response bodies may contain sensitive input; expose only classified errors.
            throw new InvalidOperationException(Error);
        }
        finally { Busy = false; _operations.Release(); Changed?.Invoke(); }
    }
    internal static string DescribeError(Exception ex) => ex switch
    {
        PluginRequestException request => request.FailureKind switch
        {
            PluginRequestFailureKind.Authentication => "Groq rejected the API key. Replace it in plugin settings.",
            PluginRequestFailureKind.Permission => "This Groq key does not have permission for the request.",
            PluginRequestFailureKind.RateLimit => "Groq rate limit reached. Wait and try again.",
            PluginRequestFailureKind.Network => "Could not reach Groq. Check your internet connection.",
            PluginRequestFailureKind.Timeout => "Groq timed out. Try again.",
            PluginRequestFailureKind.RequestTooLarge => "Recording exceeds Groq's upload limit. Use a shorter recording.",
            _ => "Groq could not complete the request. Check the selected model and try again."
        },
        OperationCanceledException => "Groq request canceled.",
        System.Reflection.TargetInvocationException { InnerException: { } inner } => DescribeError(inner),
        TypeLoadException or MissingMethodException or FileNotFoundException => "Groq package could not load: " + ex.Message,
        System.Security.Cryptography.CryptographicException => "The saved API key could not be decrypted. Remove the key and save it again.",
        IOException or UnauthorizedAccessException => "Groq settings could not be read or saved. Check storage access.",
        _ => "Groq is unavailable. Check plugin enablement, API key and model settings."
    };
    private async Task ReleaseAsync()
    {
        var lease = _lease; _lease = null;
        if (lease is not null) await lease.Lifetime.DisposeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _shutdown.Cancel();
        await _operations.WaitAsync();
        try { await ReleaseAsync(); }
        finally { _operations.Release(); _shutdown.Dispose(); }
    }
}
