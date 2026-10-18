using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>A memory source bound to its exact activation.</summary>
public sealed record PortableMemoryProvider(string PluginId, string Name, long Generation);

public sealed partial class PortablePluginRuntimeRegistry
{
    /// <summary>Enabled memory sources; enumeration never reads entries.</summary>
    public IReadOnlyList<PortableMemoryProvider> MemoryProviders
    {
        get
        {
            lock (_sync) return _slots.Values.Where(s => s.Accepting && s.Package?.Plugin is IMemoryStoragePlugin)
                .Select(s => new PortableMemoryProvider(s.Id, s.Package!.Plugin.PluginName, s.Generation)).ToArray();
        }
    }

    /// <summary>Queries the captured source under its package lease; a replacement is never substituted.</summary>
    public Task<IReadOnlyList<string>> SearchMemoryAsync(PortableMemoryProvider expected, string query, CancellationToken ct)
    {
        Slot owner;
        lock (_sync)
        {
            if (!_slots.TryGetValue(expected.PluginId, out owner!) || !owner.Accepting || owner.Generation != expected.Generation)
            {
                // A source whose package the policy unloaded meanwhile reports the policy, not a missing source.
                if (PackageRefusal(expected.PluginId) is { } refusedPackage) throw new TypeWhisper.Core.Models.ManagedPolicyDeniedException("memory", refusedPackage);
                throw new InvalidOperationException("The selected memory source is no longer available.");
            }
        }
        // The query carries transcript context, so a remote memory source follows the text-processing policy.
        DenyTextTransferIfRefused(owner, "memory");
        return UseAsync(owner, async token =>
        {
            DenyTextTransferIfRefused(owner, "memory");
            if (owner.Generation != expected.Generation || owner.Package?.Plugin is not IMemoryStoragePlugin memory)
                throw new InvalidOperationException("The selected memory source changed before execution.");
            try { return await memory.SearchAsync(query, 5, token).ConfigureAwait(false); }
            // A cancellation the policy pass caused is reported as the refusal, so the workflow keeps the reason.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { DenyTextTransferIfRefused(owner, "memory"); throw; }
        }, ct, refreshCapabilities: false);
    }
}
