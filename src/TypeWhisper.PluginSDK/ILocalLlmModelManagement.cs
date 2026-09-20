using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>Optional local LLM asset management, independent of transcription capabilities.</summary>
public interface ILocalLlmModelManagement
{
    /// <summary>Detached model states. Reading never downloads or loads a model.</summary>
    IReadOnlyList<LocalLlmModelState> LocalModels { get; }
    /// <summary>Downloads model assets with fractional progress and cooperative cancellation.</summary>
    Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Loads an existing model. A canceled load must not publish a selected model.</summary>
    Task LoadModelAsync(string modelId, CancellationToken ct);
    /// <summary>Releases model memory after draining any active inference.</summary>
    Task UnloadModelAsync(CancellationToken ct);
    /// <summary>Removes a model's assets, releasing its memory first when necessary.</summary>
    Task RemoveModelAsync(string modelId, CancellationToken ct);
}

/// <summary>One local text model's display metadata and current asset state.</summary>
public sealed record LocalLlmModelState(PluginModelInfo Model, bool Downloaded, bool Loaded);
