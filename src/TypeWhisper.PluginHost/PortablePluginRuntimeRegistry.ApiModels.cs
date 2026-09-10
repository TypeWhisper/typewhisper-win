namespace TypeWhisper.PluginHost;

public sealed partial class PortablePluginRuntimeRegistry
{
    /// <summary>Unloads native model resources under the provider lease without disabling its package.</summary>
    public Task<string?> UnloadModelAsync(string selectionId, CancellationToken cancellationToken = default)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_index.Transcription.TryGetValue(selectionId, out var role))
                throw new InvalidOperationException("The model provider is unavailable.");
            owner = role.Owner;
        }
        return UseAsync(owner, async token =>
        {
            if (!_index.Transcription.TryGetValue(selectionId, out var role) || role.Owner != owner)
                throw new InvalidOperationException("The model provider changed.");
            if (!role.Engine.SupportsModelDownload)
                throw new NotSupportedException("This provider does not support explicit model unloading.");
            token.ThrowIfCancellationRequested();
            var previous = role.Engine.SelectedModelId;
            await role.Engine.UnloadModelAsync().ConfigureAwait(false);
            if (_modelEngineIdentities.TryGetValue(role.Engine, out var identity)) identity.PossiblyLoaded.Clear();
            return previous;
        }, cancellationToken);
    }
}
