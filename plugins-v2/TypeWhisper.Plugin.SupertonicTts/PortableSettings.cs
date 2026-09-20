using System.Globalization;
using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.SupertonicTts;
public sealed partial class SupertonicTtsPlugin : IPluginTextSettings, IPluginSettingsActions
{
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        new("voice", "Voice", "Default voice for speech playback.", _selectedVoiceId)
        { Section = PluginSettingsSection.Speech, Choices = Voices.Select(v => new PluginSettingChoice(v.Id, v.DisplayName)).ToArray() },
        new("speed", "Speech speed", "0.9–1.5", Speed.ToString(CultureInfo.InvariantCulture)) { Section = PluginSettingsSection.Speech },
        new("steps", "Quality", "More denoising steps improve quality but take longer.", DenoisingSteps.ToString(CultureInfo.InvariantCulture))
        { Section = PluginSettingsSection.Speech, Choices = Enumerable.Range(1, 16).Select(n => new PluginSettingChoice(n.ToString(CultureInfo.InvariantCulture), n switch { 4 => "Fast · 4 steps", 8 => "Balanced · 8 steps", 16 => "High · 16 steps", _ => n + " steps" })).ToArray() }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "voice" && Voices.Any(v => v.Id == value)) SelectVoice(value);
        else if (id == "license" && bool.TryParse(value, out var accepted)) SetLicenseAccepted(accepted);
        else if (id == "speed" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && double.IsFinite(speed) && speed is >= 0.9 and <= 1.5) SetSpeed(speed);
        else if (id == "steps" && int.TryParse(value, out var steps) && steps is >= 1 and <= 16) SetDenoisingSteps(steps);
        else throw new ArgumentException("Invalid setting.", nameof(value));
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("download", "Download model assets", "Requires acceptance of the displayed model license first.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id != "download") throw new ArgumentException("Unknown action.", nameof(id));
        await DownloadAssetsAsync(null, ct);
        return "Model assets are ready.";
    }
}
