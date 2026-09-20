using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GraniteSpeech;

public sealed partial class GraniteSpeechPlugin
{
    private string _device = "Auto";
    private string? _activeDevice;
    /// <inheritdoc />
    public TranscriptionAccelerationStatus AccelerationStatus => new(
        _activeDevice == "cuda" ? TranscriptionAccelerationBackend.NvidiaCuda : TranscriptionAccelerationBackend.Cpu,
        _activeDevice is null ? "Model not loaded" : _activeDevice == "cuda" ? "Using CUDA" : "Using CPU",
        "Granite Speech runs locally using PyTorch.");

    private string L(string en, string de) => PortableLocalization.TryGet(_host)?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("device", L("Processing device", "Verarbeitungsgerät"),
            L("Choose the processor for the next model load. Switching a CPU-only runtime to CUDA requires downloading the updated runtime; cached model files are reused.",
              "Wähle den Prozessor für das nächste Laden. Beim Wechsel einer reinen CPU-Laufzeit zu CUDA muss die Laufzeit erneut heruntergeladen werden; vorhandene Modelldateien werden weiterverwendet."), _device)
        {
            Section = PluginSettingsSection.Transcription,
            Choices = [new("Auto", L("Automatic", "Automatisch")), new("Cpu", "CPU"), new("NvidiaCuda", "NVIDIA CUDA")]
        }
    ];

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != "device" || !TextSettings[0].Choices.Any(choice => choice.Value == value))
            throw new ArgumentException("Invalid processing device.");
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        await _sidecarLock.WaitAsync(cancellationToken);
        try
        {
            host.SetSetting("device", value);
            _device = value;
            StopSidecar();
        }
        finally { _sidecarLock.Release(); }
        host.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [new("remove-assets", L("Remove model and runtime", "Modell und Laufzeit entfernen"),
        L("Unloads the model and deletes its local model files and managed runtime. Settings are preserved.",
          "Entlädt das Modell und löscht seine lokalen Modelldateien und die verwaltete Laufzeit. Einstellungen bleiben erhalten."))];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != "remove-assets") throw new ArgumentException("Unknown action.", nameof(id));
        await RemoveModelAsync(ModelId, cancellationToken);
        return L("Model and runtime removed.", "Modell und Laufzeit entfernt.");
    }

    /// <inheritdoc />
    public Task<PluginTranscriptionResult> TranscribePcmAsync(ReadOnlyMemory<float> samples,
        string? language, bool translate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = checked(samples.Length * 2);
        var wav = new byte[checked(44 + length)];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + length);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), length);
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples.Span[i];
            if (!float.IsFinite(sample)) throw new ArgumentException("PCM samples must be finite.", nameof(samples));
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), (short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32768), short.MinValue, short.MaxValue));
        }
        return TranscribeAsync(wav, language, translate, null, cancellationToken);
    }
}
