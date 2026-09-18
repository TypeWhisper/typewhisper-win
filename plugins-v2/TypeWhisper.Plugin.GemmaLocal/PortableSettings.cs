using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.GemmaLocal;
public sealed partial class GemmaLocalPlugin : IPluginTextSettings, IPluginSettingsActions
{
    private string TargetModel => _selectedModelId ?? Models[0].Id;
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("model", "Model", "Download and load the selected model before running a workflow.", TargetModel)
        { Choices = Models.Select(m => new PluginSettingChoice(m.Id, m.DisplayName + " (" + m.SizeDescription + ")")).ToArray() }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
            case "unload": UnloadModel(); break;
            case "remove":
                if (_loadedModelId == TargetModel) UnloadModel();
                var definition = GetModelDefinition(TargetModel);
                File.Delete(GetModelFilePath(TargetModel, definition.FileName));
                break;
            default: throw new ArgumentException("Unknown action.", nameof(id));
        }
        _host?.NotifyCapabilitiesChanged();
        return _loadedModelId is null ? "No model loaded." : "Loaded: " + _loadedModelId;
    }
}
