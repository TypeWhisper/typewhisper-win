using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>Optional host-rendered management of a local speech synthesis model.</summary>
public interface ILocalTtsModelManagement : IModelDownloadRequirementsProvider
{
    /// <summary>Describes the local voice model and its download size.</summary>
    PluginModelInfo LocalModel { get; }
    /// <summary>Whether all required model assets are present.</summary>
    bool IsModelDownloaded { get; }
    /// <summary>Whether the native synthesis engine is loaded.</summary>
    bool IsModelLoaded { get; }
    /// <summary>Downloads missing assets and loads the engine. Progress is in [0, 1].</summary>
    Task DownloadAndLoadModelAsync(IProgress<double>? progress, CancellationToken cancellationToken);
    /// <summary>Releases the loaded engine while retaining downloaded assets.</summary>
    Task UnloadModelAsync(CancellationToken cancellationToken);
}
