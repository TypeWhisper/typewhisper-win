using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.GemmaLocal;
public sealed partial class GemmaLocalPlugin : IPluginTextSettings, IPluginSettingsActions
{
    private string TargetModel => _selectedModelId ?? Models[0].Id;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("threads", "CPU threads", "Applied the next time a model is loaded. Automatic uses half the available processors.", (_host?.GetSetting<int?>("threads") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
        { Choices = new[] { 0, 2, 4, 8, 16 }.Select(n => new PluginSettingChoice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == 0 ? "Automatic" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray() }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "threads")
        {
            if (!int.TryParse(value, out var threads) || !new[] { 0, 2, 4, 8, 16 }.Contains(threads)) throw new ArgumentException("Choose a supported thread count.");
            (_host ?? throw new InvalidOperationException("Activate the plugin first.")).SetSetting("threads", threads);
            return Task.CompletedTask;
        }
        if (id != "model") throw new ArgumentException("Unknown setting.", nameof(id));
        SelectModel(value);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [
        new("download", "Download selected model", "Downloads the selected GGUF model from Hugging Face."),
        new("load", "Load selected model", "Loads the downloaded model into memory."),
        new("unload", "Unload model", "Releases the model from memory."),
        new("remove", "Remove selected download", "Deletes only the selected model from this plugin's data directory.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (id)
        {
            case "download": await DownloadModelAsync(TargetModel, null, ct); break;
            case "load": await LoadModelAsync(TargetModel, ct); break;
            case "unload": await UnloadModelAsync(ct); break;
            case "remove":
                await RemoveModelAsync(TargetModel, ct);
                break;
            default: throw new ArgumentException("Unknown action.", nameof(id));
        }
        _host?.NotifyCapabilitiesChanged();
        return _loadedModelId is null ? "No model loaded." : "Loaded: " + _loadedModelId;
    }
}
