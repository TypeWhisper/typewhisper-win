using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

/// <summary>A speaker diarization provider of an enabled package, without exposing its package lifetime.</summary>
/// <param name="PluginId">The package that owns the provider.</param>
/// <param name="ProviderId">The provider's stable identifier, unique among diarization providers.</param>
/// <param name="Name">The provider's display name.</param>
/// <param name="Configured">Whether the provider can run apart from its models, for example with its API key set.</param>
/// <param name="ModelsInstalled">Whether the provider can run without downloading anything.</param>
public sealed record PortableDiarizationProvider(string PluginId, string ProviderId, string Name, bool Configured, bool ModelsInstalled)
{
    /// <summary>Whether a diarization can start now.</summary>
    public bool Ready => Configured && ModelsInstalled;
    /// <summary>Where the owning package processes audio, as declared by its manifest.</summary>
    public ContentLocality Locality { get; init; }
    /// <summary>Whether the owning package declares local processing.</summary>
    public bool IsLocal => Locality == ContentLocality.Local;
    /// <summary>The model the provider installs, for its name and size, or null.</summary>
    public PluginModelInfo? Model { get; init; }
    /// <summary>Fixed speaker counts the provider accepts besides automatic detection, or null.</summary>
    public PluginSpeakerCountRange? SpeakerCounts { get; init; }
}

public sealed partial class PortablePluginRuntimeRegistry
{
    private sealed record DiarizationRole(Slot Owner, IAudioDiarizationProvider Provider);
    // Which package served each diarization provider ID, kept after the provider is gone so a refused and unloaded
    // package still reports the policy. Guarded by _sync.
    private readonly Dictionary<string, string> _diarizationOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the last published snapshots of enabled diarization providers; listing never runs one.</summary>
    public IReadOnlyList<PortableDiarizationProvider> DiarizationProviders
    {
        get { lock (_sync) return _index.DiarizationSnapshots.Where(item => _slots[item.PluginId].Accepting).ToArray(); }
    }

    /// <summary>Why policy refuses audio for an enabled diarization provider, or null. Unknown providers return null.</summary>
    public string? DiarizationRefusal(string providerId)
    {
        lock (_sync) return _index.Diarization.TryGetValue(providerId, out var role)
            ? Policy.DiarizationRefusal(role.Owner.Id, role.Owner.Locality) : null;
    }

    /// <summary>
    /// Finds the speaker turns of a recording with an enabled provider while its package is retained. The policy is
    /// judged before the call queues, once the lease is held and when the policy pass cancels the call; a refusal throws
    /// <see cref="ManagedPolicyDeniedException"/>. A speaker count outside the provider's range throws
    /// <see cref="ArgumentOutOfRangeException"/> before any audio reaches the plugin.
    /// </summary>
    public Task<PluginDiarizationResult> DiarizeAsync(string providerId, PluginDiarizationRequest request,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = DiarizationOwner(providerId);
        DenyDiarizationIfRefused(owner);
        return UseAsync(owner, async token =>
        {
            var provider = CurrentDiarization(providerId, owner);
            DenyDiarizationIfRefused(owner);
            if (request.SpeakerCount is { } count && provider.SupportedSpeakerCounts?.Contains(count) != true)
                throw new ArgumentOutOfRangeException(nameof(request), count, "The diarization provider does not accept this speaker count.");
            try
            {
                var result = await provider.DiarizeAsync(request, progress, token).ConfigureAwait(false);
                return result ?? throw new InvalidOperationException("The diarization provider returned no result.");
            }
            // A cancellation the policy pass caused is reported as the refusal, not as a plain cancellation.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { DenyDiarizationIfRefused(owner); throw; }
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads and verifies a provider's models under its package lease. Refused while the policy turns model
    /// downloads off; a policy pass that turns them off cancels a running download and reports the refusal.
    /// </summary>
    public Task PrepareDiarizationModelsAsync(string providerId, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var owner = DiarizationOwner(providerId);
        if (Policy.ModelDownloadRefusal() is { } refused) throw new ManagedPolicyDeniedException("model-download", refused);
        return UseAsync(owner, async token =>
        {
            var provider = CurrentDiarization(providerId, owner);
            if (provider.AreDiarizationModelsInstalled) return true;
            using var reports = new ModelDownloadProgress(progress, token);
            // Marked before the final check, so a policy pass between the check and the download finds and cancels it.
            lock (_sync) owner.Downloading = true;
            try
            {
                if (Policy.ModelDownloadRefusal() is { } refusedNow) throw new ManagedPolicyDeniedException("model-download", refusedNow);
                await provider.PrepareDiarizationModelsAsync(reports, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Policy.ModelDownloadRefusal() is { } refusedMeanwhile)
            { throw new ManagedPolicyDeniedException("model-download", refusedMeanwhile); }
            finally { lock (_sync) owner.Downloading = false; }
            token.ThrowIfCancellationRequested();
            if (!provider.AreDiarizationModelsInstalled)
                throw new InvalidOperationException("The plugin did not confirm that its diarization models were installed.");
            return true;
        }, cancellationToken);
    }

    /// <summary>Unloads a provider's models and removes them from disk under its package lease.</summary>
    public Task DeleteDiarizationModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var owner = DiarizationOwner(providerId);
        return UseAsync(owner, async token =>
        {
            await CurrentDiarization(providerId, owner).DeleteDiarizationModelsAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken, preserveCompletedResult: true);
    }

    /// <summary>Frees a provider's loaded models; its files stay on disk.</summary>
    public Task UnloadDiarizationModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var owner = DiarizationOwner(providerId);
        return UseAsync(owner, async _ =>
        {
            await CurrentDiarization(providerId, owner).UnloadDiarizationModelsAsync().ConfigureAwait(false);
            return true;
        }, cancellationToken, preserveCompletedResult: true);
    }

    private Slot DiarizationOwner(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        lock (_sync)
        {
            if (_index.Diarization.TryGetValue(providerId, out var role)) return role.Owner;
            // A provider whose package the policy unloaded meanwhile reports the policy, not a missing provider.
            if (_diarizationOwners.TryGetValue(providerId, out var pluginId) && PackageRefusal(pluginId) is { } refusedPackage)
                throw new ManagedPolicyDeniedException("diarization", refusedPackage);
        }
        throw new InvalidOperationException("The diarization provider is unavailable.");
    }

    // Callers hold the package lease.
    private IAudioDiarizationProvider CurrentDiarization(string providerId, Slot owner)
    {
        if (!_index.Diarization.TryGetValue(providerId, out var current) || current.Owner != owner)
            throw new InvalidOperationException("The diarization provider changed.");
        return current.Provider;
    }

    private void DenyDiarizationIfRefused(Slot owner)
    {
        if (Policy.DiarizationRefusal(owner.Id, owner.Locality) is { } refused) throw new ManagedPolicyDeniedException("diarization", refused);
    }

    private PortableDiarizationProvider DiarizationSnapshot(Slot slot, IAudioDiarizationProvider provider)
    {
        var id = provider.DiarizationProviderId;
        if (provider.PluginId != slot.Id || string.IsNullOrWhiteSpace(id))
            throw new CapabilityCollisionException("Diarization identity is invalid or does not match its package.");
        var counts = provider.SupportedSpeakerCounts;
        if (counts is not null && (counts.Minimum < 1 || counts.Maximum < counts.Minimum)) counts = null;
        lock (_sync) _diarizationOwners[id] = slot.Id;
        return new(slot.Id, id, provider.DiarizationProviderDisplayName, provider.IsDiarizationConfigured, provider.AreDiarizationModelsInstalled)
            { Locality = slot.Locality, Model = provider.DiarizationModel, SpeakerCounts = counts };
    }
}
