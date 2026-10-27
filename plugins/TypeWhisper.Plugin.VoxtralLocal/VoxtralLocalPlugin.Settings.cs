using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.VoxtralLocal;

/// <summary>The processing device choice, shown on the plugin page like NVIDIA Parakeet's.</summary>
public sealed partial class VoxtralLocalPlugin
{
    // The host appends "In use: …" to a setting with this id when the engine runs in a worker process.
    internal const string AccelerationSetting = "acceleration";

    private string L(string english, string german) => PluginLocalization.Get(_host, english, german);

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new(AccelerationSetting, L("Processing device", "Verarbeitungsgerät"), DeviceDescription(), _preference.ToString())
        {
            Section = PluginSettingsSection.Transcription,
            SaveChoiceOnChange = true,
            Choices =
            [
                new(nameof(TranscriptionAccelerationPreference.Auto), L("Automatic", "Automatisch")),
                new(nameof(TranscriptionAccelerationPreference.AmdVulkan), L("Graphics card (Vulkan)", "Grafikkarte (Vulkan)")),
                new(nameof(TranscriptionAccelerationPreference.Cpu), "CPU")
            ]
        }
    ];

    // Only a model loaded in this process knows its device; in a worker the host adds what the worker reports.
    private string DeviceDescription()
    {
        var text = L("Automatic uses the graphics card and falls back to the processor. On the processor, Voxtral is too slow for dictation.",
            "Automatisch nutzt die Grafikkarte und weicht sonst auf den Prozessor aus. Auf dem Prozessor ist Voxtral zum Diktieren zu langsam.");
        if (_recognizer is null) return text;
        var device = _status.ActiveBackend == TranscriptionAccelerationBackend.Cpu ? "CPU" : _status.DisplayText + " (Vulkan)";
        return text + " " + L("In use: ", "In Verwendung: ") + device + (_status.Detail is { } detail ? " · " + detail : "");
    }

    // The host blocks removing the selected model, and Voxtral has only one, so the model row could never remove it.
    internal const string RemoveAction = "remove-model";

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [
        new(RemoveAction, L($"Unload and remove {_model.DisplayName}", $"{_model.DisplayName} entladen und entfernen"),
            L("Deletes the 2.8 GB model, unloading it and clearing the selection. You can download it again.",
                "Löscht das 2,8-GB-Modell, entlädt es und hebt die Auswahl auf. Du kannst es erneut herunterladen."))
        { Section = PluginSettingsSection.Transcription }
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        if (id != RemoveAction) throw new ArgumentException("Unknown Voxtral settings action.", nameof(id));
        // Checks the directory, not readiness, so an incomplete or damaged download can still be removed.
        if (_host is null || !Directory.Exists(ModelDirectory)) return L($"{_model.DisplayName} is not downloaded.", $"{_model.DisplayName} ist nicht heruntergeladen.");
        await RemoveModelAsync(_model.Id, cancellationToken).ConfigureAwait(false);
        return L($"{_model.DisplayName} removed. Download it again to use it.", $"{_model.DisplayName} entfernt. Lade es erneut herunter, um es zu nutzen.");
    }

    /// <summary>Saves the device and releases a model loaded on another one, so the next dictation loads it again.</summary>
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        if (id != AccelerationSetting || !Enum.TryParse<TranscriptionAccelerationPreference>(value, out var preference)
            || TextSettings[0].Choices.All(choice => choice.Value != value))
            throw new ArgumentException("Invalid processing device.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActive();
            _host!.SetSetting(AccelerationSetting, value);
            if (preference != _preference)
            {
                Release();
                SetAccelerationPreference(preference);
            }
            _host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }
}
