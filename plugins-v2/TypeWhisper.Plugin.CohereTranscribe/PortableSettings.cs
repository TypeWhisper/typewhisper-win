using System.Buffers.Binary;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.CohereTranscribe;

public sealed partial class CohereTranscribePlugin
{
    private string L(string en, string de) => PortableLocalization.TryGet(_host)?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => _selectedModelId is null ? [] :
    [new("remove-selected-model", L("Unload and remove selected model", "Ausgewähltes Modell entladen und entfernen"),
        L("Stops the selected model and deletes its downloaded weights. Download it again to use it later.",
          "Stoppt das ausgewählte Modell und löscht seine heruntergeladenen Gewichte. Für eine spätere Nutzung erneut herunterladen."))
        { Section = PluginSettingsSection.Transcription }];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != "remove-selected-model" || _selectedModelId is not { } selected)
            throw new InvalidOperationException("No selected model is available to remove.");
        cancellationToken.ThrowIfCancellationRequested();
        await UnloadModelAsync();
        await RemoveModelAsync(selected, cancellationToken);
        return L("Selected model removed. Shared runtime files were kept.", "Ausgewähltes Modell entfernt. Gemeinsame Laufzeitdateien wurden beibehalten.");
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("acceleration", L("Processing device", "Verarbeitungsgerät"),
            L("Transcription and live preview run locally. Choose the processor used when the model next loads.",
              "Transkription und Live-Vorschau laufen lokal. Wähle den Prozessor für das nächste Laden des Modells."), _accelerationPreference.ToString())
        {
            Section = PluginSettingsSection.Transcription,
            Choices = [new("Auto", L("Automatic", "Automatisch")), new("Cpu", "CPU"), new("NvidiaCuda", "NVIDIA CUDA"), new("AmdVulkan", "Vulkan")]
        }
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != "acceleration" || !TextSettings[0].Choices.Any(choice => choice.Value == value))
            throw new ArgumentException("Invalid processing device.");
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        host.SetSetting("acceleration", value);
        SetAccelerationPreference(Enum.Parse<TranscriptionAccelerationPreference>(value));
        host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
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
