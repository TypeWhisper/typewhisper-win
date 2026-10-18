namespace TypeWhisper.Core.Services;

/// <summary>Reads raw machine policy without validating it. Implementations never write policy.</summary>
public interface IManagedPolicySource
{
    /// <summary>Reads both scopes. Missing keys and access failures are reported in the result, not thrown.</summary>
    ManagedPolicyReading Read();

    /// <summary>Raised on any thread when the source may have changed. Readers refresh and debounce.</summary>
    event Action? Changed;
}

/// <summary>The raw content of one read.</summary>
/// <param name="Readable">False when the source could not be read; the scopes are then meaningless.</param>
/// <param name="Error">A safe technical description of a read failure, such as an exception type name.</param>
/// <param name="Enforced">The policy root.</param>
/// <param name="Recommended">The <c>Recommended</c> subkey.</param>
public sealed record ManagedPolicyReading(
    bool Readable,
    string? Error,
    ManagedPolicyRawSection Enforced,
    ManagedPolicyRawSection Recommended)
{
    /// <summary>A successful read of a machine without policy keys.</summary>
    public static ManagedPolicyReading Absent { get; } =
        new(true, null, ManagedPolicyRawSection.Missing, ManagedPolicyRawSection.Missing);

    /// <summary>A failed read.</summary>
    public static ManagedPolicyReading Unreadable(string error) =>
        new(false, error, ManagedPolicyRawSection.Missing, ManagedPolicyRawSection.Missing);
}

/// <summary>The named values and list subkeys of one scope.</summary>
/// <param name="Exists">Whether the key exists at all.</param>
/// <param name="Values">Named values by registry name. Registry names are case-insensitive.</param>
/// <param name="Lists">List subkeys by registry name.</param>
public sealed record ManagedPolicyRawSection(
    bool Exists,
    IReadOnlyDictionary<string, ManagedPolicyRawValue> Values,
    IReadOnlyDictionary<string, ManagedPolicyRawList> Lists)
{
    /// <summary>A scope whose key does not exist.</summary>
    public static ManagedPolicyRawSection Missing { get; } = new(false,
        new Dictionary<string, ManagedPolicyRawValue>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ManagedPolicyRawList>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Builds an existing scope with case-insensitive lookups.</summary>
    public static ManagedPolicyRawSection Of(
        IEnumerable<KeyValuePair<string, ManagedPolicyRawValue>>? values = null,
        IEnumerable<KeyValuePair<string, ManagedPolicyRawList>>? lists = null)
    {
        var valueMap = new Dictionary<string, ManagedPolicyRawValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in values ?? []) valueMap.TryAdd(entry.Key, entry.Value);
        var listMap = new Dictionary<string, ManagedPolicyRawList>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lists ?? []) listMap.TryAdd(entry.Key, entry.Value);
        return new(true, valueMap, listMap);
    }
}

/// <summary>The registry type class of a named value.</summary>
public enum ManagedPolicyRawKind
{
    /// <summary>REG_DWORD.</summary>
    Number,
    /// <summary>REG_SZ.</summary>
    Text,
    /// <summary>Any other registry type; always invalid.</summary>
    Unsupported
}

/// <summary>A named registry value before validation.</summary>
/// <param name="Kind">The registry type class.</param>
/// <param name="Number">The unsigned REG_DWORD content for <see cref="ManagedPolicyRawKind.Number"/>.</param>
/// <param name="Text">The REG_SZ content for <see cref="ManagedPolicyRawKind.Text"/>.</param>
/// <param name="TypeName">The registry type name for <see cref="ManagedPolicyRawKind.Unsupported"/>.</param>
public sealed record ManagedPolicyRawValue(
    ManagedPolicyRawKind Kind,
    long Number = 0,
    string? Text = null,
    string? TypeName = null)
{
    /// <summary>A REG_DWORD.</summary>
    public static ManagedPolicyRawValue FromNumber(long value) => new(ManagedPolicyRawKind.Number, value);

    /// <summary>A REG_SZ.</summary>
    public static ManagedPolicyRawValue FromText(string value) => new(ManagedPolicyRawKind.Text, Text: value);

    /// <summary>A value of any other registry type.</summary>
    public static ManagedPolicyRawValue Unsupported(string typeName) => new(ManagedPolicyRawKind.Unsupported, TypeName: typeName);
}

/// <summary>A list subkey before validation.</summary>
/// <param name="Items">The REG_SZ entries in numeric name order.</param>
/// <param name="Malformed">True when any entry is not a REG_SZ with a positive numeric name.</param>
public sealed record ManagedPolicyRawList(IReadOnlyList<string> Items, bool Malformed = false);
