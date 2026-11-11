namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional for an <see cref="ITranscriptionEnginePlugin"/>: uses a model for one host request without saving it,
/// for example a workflow's own transcription model or the model an HTTP API request names. Plugin API 1.1.8.
/// </summary>
/// <remarks>
/// A plugin that implements this interface needs a <c>minHostVersion</c> of at least 1.1.8. Without it, the host
/// selects the request's model with <see cref="ITranscriptionEnginePlugin.SelectModelAsync"/> and selects the
/// previous model again afterwards, and the plugin saves both changes as the user's choice.
/// A local engine that the host runs in a worker process gets each request's model through
/// <see cref="ITranscriptionEnginePlugin.SelectModelAsync"/> in the worker, whose host settings stay in memory. Such an
/// engine must therefore save its selection only through <see cref="IPluginHostServices.SetSetting{T}"/>, never in its
/// own files.
/// </remarks>
public interface ITranscriptionModelOverride
{
    /// <summary>
    /// Uses <paramref name="modelId"/> for transcriptions until the host clears it, without saving it and without
    /// changing the user's saved model. Meanwhile <see cref="ITranscriptionEnginePlugin.SelectedModelId"/> and the
    /// members that depend on the model, such as translation or streaming support, report this model.
    /// Null clears the override: the plugin uses the user's saved model again exactly as it would have without the
    /// override, including a saved model that its current model list does not offer.
    /// A selection through <see cref="ITranscriptionEnginePlugin.SelectModel"/> or
    /// <see cref="ITranscriptionEnginePlugin.SelectModelAsync"/> also clears the override, and deactivation ends it.
    /// </summary>
    Task SetModelOverrideAsync(string? modelId, CancellationToken ct);
}
