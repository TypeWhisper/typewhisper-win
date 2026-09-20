using TypeWhisper.PluginSDK;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal ModelDownloadController LocalLlmDownload { get; } = new();
    internal string? LocalLlmDownloadPluginId { get; private set; }
    internal string? LocalLlmDownloadModelName { get; private set; }

    internal async Task DownloadLocalLlmModelAsync(string pluginId, string modelId, string name)
    {
        if (!CanStartPluginSettingsAction || LocalLlmDownload.State.IsBusy || LocalLlmDownload.State.IsClosing)
            throw new InvalidOperationException("Finish the current model operation first.");
        LocalLlmDownloadPluginId = pluginId;
        LocalLlmDownloadModelName = name;
        await LocalLlmDownload.RunAsync((progress, ct) => PluginRuntime.UseConfigurationAsync(pluginId, async (plugin, token) =>
        {
            if (plugin is not ILocalLlmModelManagement local || !local.LocalModels.Any(model => model.Model.Id == modelId))
                throw new InvalidOperationException("The local model provider changed.");
            await local.DownloadModelAsync(modelId, progress, token);
            return true;
        }, ct, preserveCompletedResult: true));
    }
}
