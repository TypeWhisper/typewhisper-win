using System.Globalization;
using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.SupertonicTts;
public sealed partial class SupertonicTtsPlugin : IPluginTextSettings, IPluginSettingsActions
{
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        new("license", "Supertonic model license", "Review https://huggingface.co/Supertone/supertonic-3/blob/main/LICENSE (OpenRAIL-M, 2022-08-18) before accepting.", HasAcceptedModelLicense.ToString().ToLowerInvariant()) { Choices = [new("false", "Not accepted"), new("true", "I accept the model license")] },
        new("speed", "Speech speed", "0.9–1.5", Speed.ToString(CultureInfo.InvariantCulture)),
        new("steps", "Denoising steps", "1–16", DenoisingSteps.ToString(CultureInfo.InvariantCulture))];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "license" && bool.TryParse(value, out var accepted)) SetLicenseAccepted(accepted);
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
