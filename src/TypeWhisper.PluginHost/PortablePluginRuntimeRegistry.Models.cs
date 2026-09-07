using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>A detached model state bound to an exact package activation and engine instance.</summary>
public sealed record PortableDownloadableModel(string PluginId, string SelectionId, string Version, long Generation,
    Guid EngineIdentity, string ModelId, string DisplayName, string? SizeDescription, bool SupportsDownload,
    bool Downloaded, IReadOnlyList<PluginModelDownloadRequirement> Requirements);

public sealed partial class PortablePluginRuntimeRegistry
{
    private sealed class ModelEngineIdentity { internal readonly Guid Value = Guid.NewGuid(); }
    private readonly ConditionalWeakTable<ITranscriptionEnginePlugin, ModelEngineIdentity> _modelEngineIdentities = new();

    /// <summary>Reads actual model and prerequisite states under a lease, including providers that are not yet ready.</summary>
    public Task<IReadOnlyList<PortableDownloadableModel>> GetModelStatesAsync(string selectionId,
        CancellationToken cancellationToken = default)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_index.Transcription.TryGetValue(selectionId, out var role))
                throw new InvalidOperationException("The model provider is unavailable.");
            owner = role.Owner;
        }
        return UseAsync<IReadOnlyList<PortableDownloadableModel>>(owner, token =>
        {
            token.ThrowIfCancellationRequested();
            if (!_index.Transcription.TryGetValue(selectionId, out var role) || role.Owner != owner)
                throw new InvalidOperationException("The model provider changed.");
            var engine = role.Engine;
            var requirements = ReadModelRequirements(owner, engine);
            var identity = _modelEngineIdentities.GetValue(engine, static _ => new()).Value;
            var models = engine.TranscriptionModels.Select(model => new PortableDownloadableModel(owner.Id,
                selectionId, engine.PluginVersion, owner.Generation, identity, model.Id, model.DisplayName,
                model.SizeDescription, engine.SupportsModelDownload, engine.IsModelDownloaded(model.Id),
                Array.AsReadOnly(requirements.Where(item => item.ModelId == model.Id).ToArray()))).ToArray();
            return Task.FromResult<IReadOnlyList<PortableDownloadableModel>>(Array.AsReadOnly(models));
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads an explicitly captured model under its package lease. Rechecks prerequisites and actual files;
    /// never selects or loads a model. Cancellation drains the plugin and rejects its late result.
    /// </summary>
    public Task DownloadModelAsync(PortableDownloadableModel expected, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Slot owner;
        lock (_sync)
        {
            if (!_index.Transcription.TryGetValue(expected.SelectionId, out var role)
                || !MatchesModelOwner(role, expected))
                throw new InvalidOperationException("The captured model provider is no longer available.");
            owner = role.Owner;
        }
        return UseAsync(owner, async token =>
        {
            if (!_index.Transcription.TryGetValue(expected.SelectionId, out var role)
                || role.Owner != owner || !MatchesModelOwner(role, expected))
                throw new InvalidOperationException("The captured model provider changed.");
            var engine = role.Engine;
            if (!engine.SupportsModelDownload || !engine.TranscriptionModels.Any(model => model.Id == expected.ModelId))
                throw new InvalidOperationException("This model is not available for download.");
            if (ReadModelRequirements(owner, engine).Any(item => item.ModelId == expected.ModelId && item.IsRequired && !item.IsSatisfied))
                throw new InvalidOperationException("Complete this model's required configuration before downloading.");
            token.ThrowIfCancellationRequested();
            using var reports = new ModelDownloadProgress(progress, token);
            if (!engine.IsModelDownloaded(expected.ModelId))
                await engine.DownloadModelAsync(expected.ModelId, reports, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!engine.IsModelDownloaded(expected.ModelId))
                throw new InvalidOperationException("The plugin did not confirm that the model was downloaded.");
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Explicitly selects the captured model under its exact activation lease. Local assets must already exist;
    /// loading is drained on cancellation and a canceled load cannot publish a model selection.
    /// </summary>
    public Task SelectModelAsync(PortableDownloadableModel expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Slot owner;
        lock (_sync)
        {
            if (!_index.Transcription.TryGetValue(expected.SelectionId, out var role)
                || !MatchesModelOwner(role, expected))
                throw new InvalidOperationException("The captured model provider is no longer available.");
            owner = role.Owner;
        }
        return UseAsync(owner, async token =>
        {
            if (!_index.Transcription.TryGetValue(expected.SelectionId, out var role)
                || role.Owner != owner || !MatchesModelOwner(role, expected))
                throw new InvalidOperationException("The captured model provider changed.");
            var engine = role.Engine;
            if (!engine.TranscriptionModels.Any(model => model.Id == expected.ModelId))
                throw new InvalidOperationException("The selected model is no longer available.");
            token.ThrowIfCancellationRequested();
            if (engine.SupportsModelDownload)
            {
                if (!engine.IsModelDownloaded(expected.ModelId))
                    throw new InvalidOperationException("Download this model before selecting it.");
                await engine.LoadModelAsync(expected.ModelId, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (!_index.Transcription.TryGetValue(expected.SelectionId, out var current)
                || current.Owner != owner || !MatchesModelOwner(current, expected))
                throw new OperationCanceledException("The selected model provider changed during loading.", token);
            token.ThrowIfCancellationRequested();
            // The package lease remains held. SDK code may notify or wait for a worker that reads registry state;
            // never call it under _sync. UseAsync drains and rejects results superseded by concurrent disable.
            engine.SelectModel(expected.ModelId);
            return true;
        }, cancellationToken);
    }

    private sealed class ModelDownloadProgress(IProgress<double>? target, CancellationToken token) : IProgress<double>, IDisposable
    {
        private readonly object _sync = new();
        private bool _finished;
        public void Report(double value)
        {
            lock (_sync)
                if (!_finished && !token.IsCancellationRequested && double.IsFinite(value) && value >= 0 && value <= 1)
                    target?.Report(value);
        }
        public void Dispose() { lock (_sync) _finished = true; }
    }

    private bool MatchesModelOwner(TranscriptionRole role, PortableDownloadableModel expected) =>
        role.Owner.Accepting && role.Owner.Id == expected.PluginId && role.Owner.Generation == expected.Generation
        && role.Engine.PluginVersion == expected.Version
        && _modelEngineIdentities.GetValue(role.Engine, static _ => new()).Value == expected.EngineIdentity;

    private static PluginModelDownloadRequirement[] ReadModelRequirements(Slot owner, ITranscriptionEnginePlugin engine)
    {
        var result = new List<PluginModelDownloadRequirement>();
        if (owner.Package!.Plugin is IModelDownloadRequirementsProvider packageRequirements)
            result.AddRange(packageRequirements.ModelDownloadRequirements);
        if (!ReferenceEquals(owner.Package.Plugin, engine) && engine is IModelDownloadRequirementsProvider engineRequirements)
            result.AddRange(engineRequirements.ModelDownloadRequirements);
        return result.ToArray();
    }
}
