using System.Collections.Frozen;
using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// Turns the managed policy keys modules declare into definitions the validator checks with the host's own keys.
// Registry names ignore case, so a name may stand for one key only, and a list also takes its Configured marker.
internal static class ModulePolicyKeys
{
    internal const string RejectedStage = "modules.policy-key.rejected";
    private const int MaximumNameLength = 128;
    // The subkey that holds recommendations; no key may use its name.
    private const string RecommendedSubkey = "Recommended";

    // Definitions: the keys the host now validates for modules. DeclaredNames: the names modules may read, which
    // also includes host keys a module declared with the same kind.
    internal sealed record Resolution(IReadOnlyList<ManagedPolicyDefinition> Definitions, IReadOnlySet<string> DeclaredNames)
    {
        internal static Resolution None { get; } = new([], FrozenSet<string>.Empty);
    }

    // Who uses a registry name: a key, or the Configured marker of a list.
    private sealed record Owner(string Name, ModuleManagedPolicyKind? Kind, ModuleManagedPolicyKey? ModuleKey, bool IsMarker);

    // reject receives the key name, or null when the name itself is invalid, and a short reason. Never values.
    internal static Resolution Resolve(IEnumerable<ModuleManagedPolicyKey> keys, Action<string?, string> reject)
    {
        var owners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in ManagedPolicyKeys.All)
        {
            owners[host.Name] = new(host.Name, ToModuleKind(host.Kind), null, false);
            if (host.Kind == ManagedPolicyValueKind.List) owners[host.ConfiguredMarkerName] = new(host.ConfiguredMarkerName, null, null, true);
        }
        var definitions = new List<ManagedPolicyDefinition>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key is null) continue;
            if (!IsValidName(key.Name)) { reject(null, "invalid-name"); continue; }
            var name = key.Name;
            if (!Enum.IsDefined(key.Kind)) { reject(name, "unknown-kind"); continue; }
            if (string.Equals(name, RecommendedSubkey, StringComparison.OrdinalIgnoreCase)) { reject(name, "reserved-name"); continue; }
            if (key.Kind == ModuleManagedPolicyKind.Enumeration && key.EnumerationMaximum < 1) { reject(name, "enumeration-maximum"); continue; }

            if (owners.TryGetValue(name, out var owner))
            {
                var reason = owner.IsMarker ? "marker-conflict"
                    : !string.Equals(owner.Name, name, StringComparison.Ordinal) ? "case-conflict"
                    : owner.Kind != key.Kind ? "kind-conflict"
                    // The host keeps its own definition; a module that declares the same key again must declare it alike.
                    : owner.ModuleKey is { } earlier && earlier != key ? "declaration-conflict"
                    : null;
                if (reason is null) declared.Add(name);
                else reject(name, reason);
                continue;
            }
            var marker = name + "Configured";
            if (key.Kind == ModuleManagedPolicyKind.List && owners.ContainsKey(marker)) { reject(name, "marker-conflict"); continue; }

            owners[name] = new(name, key.Kind, key, false);
            if (key.Kind == ModuleManagedPolicyKind.List) owners[marker] = new(marker, null, null, true);
            definitions.Add(new(name, ToHostKind(key.Kind), key.AllowsRecommendation,
                key.Kind == ModuleManagedPolicyKind.Enumeration ? key.EnumerationMaximum : 0, Sensitive: key.Sensitive));
            declared.Add(name);
        }
        return new(definitions.AsReadOnly(), declared.ToFrozenSet(StringComparer.Ordinal));
    }

    // ASCII letters and digits, starting with a letter, at most 128 characters.
    internal static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaximumNameLength } && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);

    // What the policy says about a declared key. Names no module declared read as unmanaged, host keys included.
    internal static ModuleManagedPolicyState State(ManagedPolicySnapshot snapshot, IReadOnlySet<string> declaredNames, string name)
    {
        if (name is null || !declaredNames.Contains(name)) return ModuleManagedPolicyState.Unmanaged;
        var enforced = ToModuleValue(snapshot.Enforced(name));
        var recommended = ToModuleValue(snapshot.Recommended(name));
        var blocked = snapshot.IsBlocked(name);
        return enforced is null && recommended is null && !blocked
            ? ModuleManagedPolicyState.Unmanaged
            : new(enforced, recommended, blocked);
    }

    internal static ModuleManagedPolicyValue? ToModuleValue(ManagedPolicyValue? value) => value?.Kind switch
    {
        null => null,
        ManagedPolicyValueKind.Boolean => new ModuleManagedPolicyValue.Boolean(value.IsTrue),
        ManagedPolicyValueKind.Enumeration => new ModuleManagedPolicyValue.Enumeration(value.Number),
        ManagedPolicyValueKind.Text => new ModuleManagedPolicyValue.Text(value.Text!),
        ManagedPolicyValueKind.List => new ModuleManagedPolicyValue.List(value.Items!),
        _ => null
    };

    private static ManagedPolicyValueKind ToHostKind(ModuleManagedPolicyKind kind) => kind switch
    {
        ModuleManagedPolicyKind.Boolean => ManagedPolicyValueKind.Boolean,
        ModuleManagedPolicyKind.Enumeration => ManagedPolicyValueKind.Enumeration,
        ModuleManagedPolicyKind.Text => ManagedPolicyValueKind.Text,
        ModuleManagedPolicyKind.List => ManagedPolicyValueKind.List,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ModuleManagedPolicyKind ToModuleKind(ManagedPolicyValueKind kind) => kind switch
    {
        ManagedPolicyValueKind.Boolean => ModuleManagedPolicyKind.Boolean,
        ManagedPolicyValueKind.Enumeration => ModuleManagedPolicyKind.Enumeration,
        ManagedPolicyValueKind.Text => ModuleManagedPolicyKind.Text,
        ManagedPolicyValueKind.List => ModuleManagedPolicyKind.List,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

// IManagedPolicyReading over the process-wide policy. DeclaredNames stays empty until every module has contributed,
// so every key reads as unmanaged during Contribute, as the interface documents.
internal sealed class ModuleManagedPolicyReading : IManagedPolicyReading
{
    private readonly Func<ManagedPolicySnapshot> _current;
    private readonly Action _refresh;
    private readonly Action<Action<ManagedPolicySnapshot>> _subscribe;
    private readonly Action<Action<ManagedPolicySnapshot>> _unsubscribe;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception> _report;
    private IReadOnlySet<string> _declaredNames = FrozenSet<string>.Empty;

    internal ModuleManagedPolicyReading(Func<ManagedPolicySnapshot> current, Action refresh,
        Action<Action<ManagedPolicySnapshot>> subscribe, Action<Action<ManagedPolicySnapshot>> unsubscribe,
        Action<Action> post, Action<string, Exception> report)
    {
        _current = current;
        _refresh = refresh;
        _subscribe = subscribe;
        _unsubscribe = unsubscribe;
        _post = post;
        _report = report;
    }

    internal IReadOnlySet<string> DeclaredNames
    {
        get => Volatile.Read(ref _declaredNames);
        set => Volatile.Write(ref _declaredNames, value);
    }

    public ModuleManagedPolicyState State(string name) => ModulePolicyKeys.State(_current(), DeclaredNames, name);

    public void Refresh() => _refresh();

    public IDisposable ObserveChanges(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var observation = new ModuleObservation(_post, _report, "modules.policy.observer-failed");
        Action<ManagedPolicySnapshot> listener = _ => observation.Deliver(handler);
        _subscribe(listener);
        observation.Unsubscribe = () => _unsubscribe(listener);
        return observation;
    }
}
