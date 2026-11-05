using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>A dictation target role bound to one exact package activation.</summary>
/// <param name="PluginId">The owning package.</param>
/// <param name="Name">The plugin's display name.</param>
/// <param name="ProcessNames">Process names, without ".exe", of the apps the plugin handles.</param>
/// <param name="Generation">The activation the role belongs to.</param>
public sealed record PortableDictationTarget(string PluginId, string Name, IReadOnlyList<string> ProcessNames, long Generation)
{
    /// <summary>Whether a dictation into the process belongs to this target.</summary>
    public bool Handles(string? processName) => !string.IsNullOrWhiteSpace(processName)
        && ProcessNames.Contains(processName.Trim(), StringComparer.OrdinalIgnoreCase);
}

public sealed partial class PortablePluginRuntimeRegistry
{
    private sealed record DictationTargetRole(Slot Owner, IDictationTargetPlugin Plugin, PortableDictationTarget Snapshot);

    /// <summary>
    /// Enabled packages that take over dictation into the apps they list, in identifier order. A package the policy
    /// does not allow text is left out, so a dictation into its apps stays with the host.
    /// </summary>
    public IReadOnlyList<PortableDictationTarget> DictationTargets
    {
        get
        {
            lock (_sync) return _index.DictationTargets.Values
                .Where(role => role.Owner.Accepting && !role.Owner.Stopping && Policy.TextTransferRefusal(role.Owner.Id, role.Owner.Locality) is null)
                .Select(role => role.Snapshot with { Generation = role.Owner.Generation })
                .OrderBy(item => item.PluginId, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>
    /// Calls a dictation target beside the queue that serializes other plugin calls: live transcription holds that
    /// queue while the user speaks, and the target must hear about the dictation then. Disabling or unloading the
    /// package cancels running calls, and its release waits until they returned. Throws
    /// <see cref="InvalidOperationException"/> when the activation is gone or the policy refuses it text.
    /// </summary>
    public async Task<T> UseDictationTargetAsync<T>(PortableDictationTarget expected,
        Func<IDictationTargetPlugin, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        var (owner, plugin, lifetime) = EnterDictationTarget(expected);
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            return await use(plugin, request.Token).ConfigureAwait(false);
        }
        finally { ExitDictationTarget(owner); }
    }

    /// <summary>
    /// Calls a dictation target synchronously, for calls that must return at once, such as a step reported on the UI
    /// thread. Returns false without calling the plugin when its activation is gone or the policy refuses it text.
    /// </summary>
    public bool TryUseDictationTarget<T>(PortableDictationTarget expected, Func<IDictationTargetPlugin, T> use, out T? result)
    {
        result = default;
        Slot owner;
        IDictationTargetPlugin plugin;
        try { (owner, plugin, _) = EnterDictationTarget(expected); }
        catch (InvalidOperationException) { return false; }
        try
        {
            result = use(plugin);
            return true;
        }
        finally { ExitDictationTarget(owner); }
    }

    private (Slot Owner, IDictationTargetPlugin Plugin, CancellationToken Lifetime) EnterDictationTarget(PortableDictationTarget expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_sync)
        {
            if (_disposed || !_index.DictationTargets.TryGetValue(expected.PluginId, out var role) || !role.Owner.Accepting
                || role.Owner.Stopping || role.Owner.Generation != expected.Generation)
                throw new InvalidOperationException("The dictation target is no longer available.");
            // The plugin receives the dictation's text, so the text rule decides as it does for processors and actions.
            DenyTextTransferIfRefused(role.Owner, "dictation-target");
            role.Owner.TargetCalls++;
            return (role.Owner, role.Plugin, role.Owner.TargetLifetime.Token);
        }
    }

    private void ExitDictationTarget(Slot owner)
    {
        lock (_sync)
        {
            if (--owner.TargetCalls > 0 || owner.TargetIdle is not { } idle) return;
            owner.TargetIdle = null;
            idle.TrySetResult();
        }
    }

    // Cancels the running target calls of a package that stops accepting or loses a role; later calls get a fresh
    // lifetime. Callers hold _sync, so cancellation callbacks run outside it.
    private static void CancelDictationTargetCalls(Slot slot)
    {
        var running = slot.TargetLifetime;
        slot.TargetLifetime = new();
        _ = ObserveCancellationAsync(running.CancelAsync());
    }

    // Completes once no target call runs any more. The slot no longer accepts calls, so none can start meanwhile.
    private Task DictationTargetCallsReturnedAsync(Slot slot)
    {
        lock (_sync)
        {
            if (slot.TargetCalls == 0) return Task.CompletedTask;
            slot.TargetIdle ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return slot.TargetIdle.Task;
        }
    }
}
