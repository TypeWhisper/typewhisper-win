using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.WhisperCpp;

public sealed partial class WhisperCppPlugin
{
    private string L(string en, string de) => PortableLocalization.TryGet(_host)?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("acceleration", L("Processing device", "Verarbeitungsgerät"),
            IsCudaRuntimeRestartRequired
                ? L("CUDA support was installed successfully. Restart TypeWhisper, then select this model again.",
                    "CUDA-Unterstützung wurde erfolgreich installiert. Starte TypeWhisper neu und wähle danach das Modell erneut aus.")
                : L("Local transcription with whisper.cpp. Choose the processor used for transcription. Changing a loaded runtime may require an app restart.",
              "Lokale Transkription mit whisper.cpp. Wähle den Prozessor für die Transkription. Der Wechsel einer geladenen Laufzeit kann einen App-Neustart erfordern.")
                + (CurrentDevice is { } current ? " " + L("In use: ", "In Verwendung: ") + current : ""),
            _accelerationPreference.ToString())
        {
            Section = PluginSettingsSection.Transcription,
            Choices = [new("Auto", L("Automatic", "Automatisch")), new("Cpu", "CPU"), new("NvidiaCuda", "NVIDIA CUDA"),
                new("AmdVulkan", "Vulkan"), new("AmdRocm", "AMD ROCm")]
        }
    ];

    // What the loaded model actually runs on, including the GPU and why a GPU choice fell back to CPU.
    private string? CurrentDevice => _factory is null ? null : _accelerationStatus.ActiveBackend switch
    {
        TranscriptionAccelerationBackend.AmdVulkan => "Vulkan" + (_gpuDevice is { } gpu
            ? " · " + gpu.Name + (gpu.Integrated ? L(" (integrated graphics)", " (integrierte Grafik)") : "") : ""),
        TranscriptionAccelerationBackend.NvidiaCuda => "NVIDIA CUDA",
        TranscriptionAccelerationBackend.AmdRocm => "AMD ROCm",
        _ when _accelerationPreference is TranscriptionAccelerationPreference.Cpu or TranscriptionAccelerationPreference.Auto => "CPU",
        _ => $"CPU ({_accelerationStatus.DisplayText})"
    };

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id != "acceleration" || !TextSettings[0].Choices.Any(c => c.Value == value)) throw new ArgumentException("Invalid processing device.");
        var host = _host ?? throw new InvalidOperationException("Activate the plugin first.");
        host.SetSetting("acceleration", value);
        var preference = Enum.Parse<TranscriptionAccelerationPreference>(value);
        if (preference != _accelerationPreference) SetAccelerationPreference(preference);
        host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    // Whisper multilingual language codes; English-only weights expose only English.
    private static readonly IReadOnlyList<string> WhisperLanguages = Array.AsReadOnly(new[]
    {
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi",
        "fi", "vi", "he", "uk", "el", "ms", "cs", "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la",
        "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk", "br", "eu", "is", "hy",
        "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be",
        "tg", "sd", "gu", "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl",
        "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su"
    });
}
