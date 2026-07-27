using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.ViewModels;

internal enum LiveTranscriptionStartupMode
{
    None,
    PluginStreaming,
    PluginPollingFallback,
    LegacyVad
}

internal static class LiveTranscriptionStartupPolicy
{
    /// <summary>
    /// Performs select.
    /// </summary>
    public static LiveTranscriptionStartupMode Select(
        AppSettings settings,
        bool isPluginModel,
        ITranscriptionEnginePlugin? plugin,
        string? prompt = null)
    {
        if (!settings.LiveTranscriptionEnabled)
            return LiveTranscriptionStartupMode.None;

        if (!isPluginModel)
            return LiveTranscriptionStartupMode.LegacyVad;

        if (plugin is null)
            return LiveTranscriptionStartupMode.None;

        if (plugin.SupportsStreamingForPrompt(prompt))
            return LiveTranscriptionStartupMode.PluginStreaming;

        if (plugin.SupportsModelDownload || settings.OnlineAsrBatchLiveTranscriptionEnabled)
            return LiveTranscriptionStartupMode.PluginPollingFallback;

        return LiveTranscriptionStartupMode.None;
    }
}
