namespace TypeWhisper.ModuleKit;

/// <summary>Reads the managed policy keys that modules declare in <see cref="ModuleContributions.ManagedPolicyKeys"/>.</summary>
/// <remarks>
/// The host reads, validates and reports these keys like its own, from <c>HKLM\SOFTWARE\Policies\TypeWhisper</c> and its
/// <c>Recommended</c> subkey. Keys that no module declared have no value here, including host keys a module did not
/// declare. The host reads the declared keys only after every module has contributed, so during
/// <see cref="IHostModule.Contribute"/> every key reads as unmanaged.
/// </remarks>
public interface IManagedPolicyReading
{
    /// <summary>What the managed policy says about a declared key.</summary>
    /// <param name="name">The key name, compared exactly.</param>
    ModuleManagedPolicyState State(string name);

    /// <summary>Asks the host to read the policy again, for example right before a request that depends on it.</summary>
    void Refresh();

    /// <summary>Calls <paramref name="handler"/> on the UI thread after the managed policy changed, until the result is disposed.</summary>
    IDisposable ObserveChanges(Action handler);
}

/// <summary>The value shape of a managed policy key. The same shapes as the host's own keys.</summary>
public enum ModuleManagedPolicyKind
{
    /// <summary>A REG_DWORD that is exactly 0 or 1.</summary>
    Boolean,
    /// <summary>A REG_DWORD from 0 to <see cref="ModuleManagedPolicyKey.EnumerationMaximum"/>.</summary>
    Enumeration,
    /// <summary>A trimmed, non-empty REG_SZ without control characters.</summary>
    Text,
    /// <summary>A subkey of numbered REG_SZ values, enabled by a sibling REG_DWORD <c>&lt;Name&gt;Configured</c>.</summary>
    List
}

/// <summary>A managed policy key a module reads.</summary>
/// <remarks>
/// A module may declare one of the host's own keys with the same kind to read it; the host keeps its own definition.
/// A key whose name the host or an earlier module already uses with another kind is left out and reported.
/// </remarks>
/// <param name="Name">The exact registry value or subkey name: ASCII letters and digits, starting with a letter, at most 128 characters.</param>
/// <param name="Kind">The value shape.</param>
/// <param name="AllowsRecommendation">Whether the key may also appear under <c>Recommended</c>. Restrictions may not.</param>
/// <param name="EnumerationMaximum">The largest valid value of an enumeration.</param>
/// <param name="Sensitive">Whether the value must never appear in diagnostics, logs or exports.</param>
public sealed record ModuleManagedPolicyKey(
    string Name,
    ModuleManagedPolicyKind Kind,
    bool AllowsRecommendation = false,
    int EnumerationMaximum = 0,
    bool Sensitive = false);

/// <summary>A validated managed policy value.</summary>
public abstract record ModuleManagedPolicyValue
{
    private ModuleManagedPolicyValue() { }

    /// <summary>A <see cref="ModuleManagedPolicyKind.Boolean"/> value.</summary>
    public sealed record Boolean(bool Value) : ModuleManagedPolicyValue;

    /// <summary>A <see cref="ModuleManagedPolicyKind.Enumeration"/> value.</summary>
    public sealed record Enumeration(int Value) : ModuleManagedPolicyValue;

    /// <summary>A <see cref="ModuleManagedPolicyKind.Text"/> value.</summary>
    public sealed record Text(string Value) : ModuleManagedPolicyValue;

    /// <summary>A <see cref="ModuleManagedPolicyKind.List"/> value: trimmed, deduplicated entries in their order.</summary>
    public sealed record List(IReadOnlyList<string> Items) : ModuleManagedPolicyValue
    {
        /// <inheritdoc />
        public bool Equals(List? other) => other is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in Items) hash.Add(item, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

/// <summary>What the managed policy says about one key.</summary>
/// <param name="Enforced">A value users cannot change, or null.</param>
/// <param name="Recommended">A default an administrator recommends and users may change, or null.</param>
/// <param name="IsBlocked">
/// The policy could not be read, or the enforced value is invalid. What the key controls must then stay off.
/// </param>
public sealed record ModuleManagedPolicyState(
    ModuleManagedPolicyValue? Enforced = null,
    ModuleManagedPolicyValue? Recommended = null,
    bool IsBlocked = false)
{
    /// <summary>A key without any managed value.</summary>
    public static ModuleManagedPolicyState Unmanaged { get; } = new();

    /// <summary>
    /// The enforced value, else the user's choice, else the recommendation, else <paramref name="defaultValue"/>.
    /// Check <see cref="IsBlocked"/> first.
    /// </summary>
    public bool BooleanValue(bool defaultValue, bool? userPreference = null) =>
        Enforced is ModuleManagedPolicyValue.Boolean enforced ? enforced.Value
        : userPreference ?? (Recommended is ModuleManagedPolicyValue.Boolean recommended ? recommended.Value : defaultValue);
}
