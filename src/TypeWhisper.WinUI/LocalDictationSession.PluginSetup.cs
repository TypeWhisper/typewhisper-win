using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    internal async Task<string?> InstallSetupPluginAsync(IProgress<PluginInstallationProgress> progress, CancellationToken ct)
    {
        if (!CanChangeProvider || Packages.Updates.Busy || !await _gate.WaitAsync(0, ct))
            return "Finish the current operation before installing the plugin.";
        try
        {
            SetStatus("Installing NVIDIA Parakeet…", DictationPhase.Configuring);
            if (!Packages.Store.IsInstalled(LocalTranscriptionPlugin.PluginId))
            {
                var entries = await Packages.Catalog.FetchAsync(ct);
                var entry = entries.SingleOrDefault(item => item.Id == LocalTranscriptionPlugin.PluginId
                    && item.Supports(LocalCtcVocabulary.HostVersion, PortablePluginCatalog.Architecture));
                if (entry is null) return "No compatible NVIDIA Parakeet plugin is available. Please try again later.";
                if (await Packages.Store.InstallAsync(entry, progress, ct))
                    return "Restart TypeWhisper to finish installing the plugin.";
            }
            ct.ThrowIfCancellationRequested();
            if (_disposed) return "The application is shutting down.";
            await _transcriptionPlugin.SetEnabledAsync(true);
            progress.Report(new("Preparing dictionary boosting…"));
            var vocabularyError = await CtcVocabulary.SetEnabledAsync(Models.Enabled, ct);
            LocalPluginError = Models.Error ?? vocabularyError;
            return LocalPluginError;
        }
        catch (OperationCanceledException) { return "Plugin installation cancelled. You can retry at any time."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return "Could not install the plugin: " + ex.Message; }
        finally
        {
            SetStatus(IsReady ? $"{ActiveModelName} ready" : "Choose and download a model to start dictating.", DictationPhase.Idle);
            _gate.Release();
        }
    }
}
