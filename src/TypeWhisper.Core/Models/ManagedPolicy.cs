using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Models;

/// <summary>The value shape of a managed policy key.</summary>
public enum ManagedPolicyValueKind
{
    /// <summary>A REG_DWORD that is exactly 0 or 1.</summary>
    Boolean,
    /// <summary>A REG_DWORD within a key-specific range that starts at 0.</summary>
    Enumeration,
    /// <summary>A trimmed, non-empty REG_SZ without control characters.</summary>
    Text,
    /// <summary>A subkey of numbered REG_SZ values, enabled by a sibling <c>&lt;Name&gt;Configured</c> marker.</summary>
    List
}

/// <summary>Where a managed policy value is declared.</summary>
public enum ManagedPolicyScope
{
    /// <summary>The policy root: values users cannot change.</summary>
    Enforced,
    /// <summary>The <c>Recommended</c> subkey: administrator defaults users may change.</summary>
    Recommended
}

/// <summary>Whether the policy source could be read and whether it holds anything.</summary>
public enum ManagedPolicyReadStatus
{
    /// <summary>The source was read and contains no policy keys.</summary>
    Absent,
    /// <summary>The source was read and contains at least one policy key.</summary>
    Present,
    /// <summary>The source could not be read. Retained values may be stale and managed operations should wait.</summary>
    Unreadable
}

/// <summary>Describes one supported managed policy key.</summary>
/// <param name="Name">The registry value or subkey name.</param>
/// <param name="Kind">The value shape.</param>
/// <param name="AllowsRecommendation">Whether the key may appear under <c>Recommended</c>. Restrictions may not.</param>
/// <param name="EnumerationMaximum">The largest valid enumeration value.</param>
/// <param name="PairedWith">A key that must be present at the same scope, or null.</param>
/// <param name="Sensitive">Whether the value must never appear in diagnostics, logs or exports.</param>
public sealed record ManagedPolicyDefinition(
    string Name,
    ManagedPolicyValueKind Kind,
    bool AllowsRecommendation = false,
    int EnumerationMaximum = 0,
    string? PairedWith = null,
    bool Sensitive = false)
{
    /// <summary>The REG_DWORD marker that enables a list. Absent or 0 leaves the list unrestricted.</summary>
    public string ConfiguredMarkerName => Name + "Configured";
}

/// <summary>The keys supported under <c>HKLM\SOFTWARE\Policies\TypeWhisper</c>, as specified in issue #470.</summary>
public static class ManagedPolicyKeys
{
    /// <summary>Stable transcription role selection ID; enforced or recommended together with <see cref="TranscriptionModelId"/>.</summary>
    public const string TranscriptionProviderId = "TranscriptionProviderId";
    /// <summary>Transcription model ID; enforced or recommended together with <see cref="TranscriptionProviderId"/>.</summary>
    public const string TranscriptionModelId = "TranscriptionModelId";
    /// <summary>Stable LLM role selection ID; enforced or recommended together with <see cref="LlmModelId"/>.</summary>
    public const string LlmProviderId = "LlmProviderId";
    /// <summary>LLM model ID; enforced or recommended together with <see cref="LlmProviderId"/>.</summary>
    public const string LlmModelId = "LlmModelId";
    /// <summary>Stable transcription role IDs that may be used at any entry point.</summary>
    public const string AllowedTranscriptionProviders = "AllowedTranscriptionProviders";
    /// <summary>Stable LLM role IDs that may be used at any entry point.</summary>
    public const string AllowedLlmProviders = "AllowedLlmProviders";
    /// <summary>Package IDs that may be installed, loaded, enabled and executed.</summary>
    public const string AllowedPlugins = "AllowedPlugins";
    /// <summary>0 blocks remote audio processing.</summary>
    public const string AllowCloudTranscription = "AllowCloudTranscription";
    /// <summary>0 blocks remote processing of transcript, prompt, vocabulary and contextual text.</summary>
    public const string AllowCloudTextProcessing = "AllowCloudTextProcessing";
    /// <summary>0 blocks app-initiated plugin acquisition and updates.</summary>
    public const string AllowPluginDownloads = "AllowPluginDownloads";
    /// <summary>0 blocks app-initiated model and runtime asset acquisition.</summary>
    public const string AllowModelDownloads = "AllowModelDownloads";
    /// <summary>0 = External, 1 = CheckOnly, 2 = UserInitiated.</summary>
    public const string UpdateMode = "UpdateMode";
    /// <summary>0 = Stable, 1 = Daily, 2 = ReleaseCandidate.</summary>
    public const string UpdateChannel = "UpdateChannel";
    /// <summary>Channel names that intersect with the channel selection; an empty configured list blocks app updates.</summary>
    public const string AllowedUpdateChannels = "AllowedUpdateChannels";
    /// <summary>Enforces or recommends app-owned startup registration.</summary>
    public const string LaunchAtLogin = "LaunchAtLogin";
    /// <summary>1 suppresses initial setup only when its prerequisites are met. Enforced only.</summary>
    public const string SkipOnboardingWhenReady = "SkipOnboardingWhenReady";
    /// <summary>1 hides optional welcome and promotional prompts.</summary>
    public const string SuppressOptionalPrompts = "SuppressOptionalPrompts";

    /// <summary>Every supported key in documentation order.</summary>
    public static IReadOnlyList<ManagedPolicyDefinition> All { get; } =
    [
        new(TranscriptionProviderId, ManagedPolicyValueKind.Text, AllowsRecommendation: true, PairedWith: TranscriptionModelId),
        new(TranscriptionModelId, ManagedPolicyValueKind.Text, AllowsRecommendation: true, PairedWith: TranscriptionProviderId),
        new(LlmProviderId, ManagedPolicyValueKind.Text, AllowsRecommendation: true, PairedWith: LlmModelId),
        new(LlmModelId, ManagedPolicyValueKind.Text, AllowsRecommendation: true, PairedWith: LlmProviderId),
        new(AllowedTranscriptionProviders, ManagedPolicyValueKind.List),
        new(AllowedLlmProviders, ManagedPolicyValueKind.List),
        new(AllowedPlugins, ManagedPolicyValueKind.List),
        new(AllowCloudTranscription, ManagedPolicyValueKind.Boolean),
        new(AllowCloudTextProcessing, ManagedPolicyValueKind.Boolean),
        new(AllowPluginDownloads, ManagedPolicyValueKind.Boolean),
        new(AllowModelDownloads, ManagedPolicyValueKind.Boolean),
        new(UpdateMode, ManagedPolicyValueKind.Enumeration, EnumerationMaximum: 2),
        new(UpdateChannel, ManagedPolicyValueKind.Enumeration, AllowsRecommendation: true, EnumerationMaximum: 2),
        new(AllowedUpdateChannels, ManagedPolicyValueKind.List),
        new(LaunchAtLogin, ManagedPolicyValueKind.Boolean, AllowsRecommendation: true),
        new(SkipOnboardingWhenReady, ManagedPolicyValueKind.Boolean),
        new(SuppressOptionalPrompts, ManagedPolicyValueKind.Boolean, AllowsRecommendation: true)
    ];

    private static readonly Dictionary<string, ManagedPolicyDefinition> ByName =
        All.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

    /// <summary>Finds a definition by its exact name, or null for an unknown key.</summary>
    public static ManagedPolicyDefinition? Find(string name) => ByName.GetValueOrDefault(name);
}

/// <summary>A validated policy value of one kind, compared by content.</summary>
public sealed class ManagedPolicyValue : IEquatable<ManagedPolicyValue>
{
    private ManagedPolicyValue(ManagedPolicyValueKind kind, int number, string? text, IReadOnlyList<string>? items)
    {
        Kind = kind;
        Number = number;
        Text = text;
        Items = items;
    }

    /// <summary>The value shape.</summary>
    public ManagedPolicyValueKind Kind { get; }

    /// <summary>0 or 1 for booleans, the selected member for enumerations, otherwise 0.</summary>
    public int Number { get; }

    /// <summary>The trimmed text for <see cref="ManagedPolicyValueKind.Text"/>, otherwise null.</summary>
    public string? Text { get; }

    /// <summary>The deduplicated entries for <see cref="ManagedPolicyValueKind.List"/>, otherwise null.</summary>
    public IReadOnlyList<string>? Items { get; }

    /// <summary>Whether this is a boolean set to 1.</summary>
    public bool IsTrue => Kind == ManagedPolicyValueKind.Boolean && Number == 1;

    /// <summary>Creates a boolean value.</summary>
    public static ManagedPolicyValue ForBoolean(bool value) => new(ManagedPolicyValueKind.Boolean, value ? 1 : 0, null, null);

    /// <summary>Creates an enumeration value.</summary>
    public static ManagedPolicyValue ForEnumeration(int value) => new(ManagedPolicyValueKind.Enumeration, value, null, null);

    /// <summary>Creates a text value. Callers pass validated, trimmed text.</summary>
    public static ManagedPolicyValue ForText(string value) => new(ManagedPolicyValueKind.Text, 0, value, null);

    /// <summary>Creates a list value. Callers pass validated, deduplicated entries.</summary>
    public static ManagedPolicyValue ForList(IReadOnlyList<string> items) => new(ManagedPolicyValueKind.List, 0, null, Array.AsReadOnly(items.ToArray()));

    /// <inheritdoc />
    public bool Equals(ManagedPolicyValue? other) =>
        other is not null && Kind == other.Kind && Number == other.Number &&
        string.Equals(Text, other.Text, StringComparison.Ordinal) &&
        (Items is null ? other.Items is null : other.Items is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ManagedPolicyValue);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Number);
        hash.Add(Text, StringComparer.Ordinal);
        if (Items is not null)
            foreach (var item in Items) hash.Add(item, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>A validation finding about the policy source. Messages name keys, never values.</summary>
/// <param name="Key">The affected key, or null for the source as a whole.</param>
/// <param name="Scope">The affected scope, or null for the source as a whole.</param>
/// <param name="Message">A technical English description for diagnostics.</param>
public sealed record ManagedPolicyIssue(string? Key, ManagedPolicyScope? Scope, string Message);

/// <summary>An immutable, validated view of the machine policy at one point in time.</summary>
/// <remarks>
/// Consumers check <see cref="IsBlocked"/> before relying on a resolved value: an unreadable source or an
/// invalid enforced value must block the affected capability instead of relaxing it.
/// </remarks>
public sealed class ManagedPolicySnapshot
{
    /// <summary>The state before the first read: revision 0 and no policy.</summary>
    public static ManagedPolicySnapshot Empty { get; } =
        new(0, ManagedPolicyReadStatus.Absent, DateTimeOffset.MinValue,
            System.Collections.ObjectModel.ReadOnlyDictionary<string, ManagedPolicyValue>.Empty,
            System.Collections.ObjectModel.ReadOnlyDictionary<string, ManagedPolicyValue>.Empty,
            System.Collections.Frozen.FrozenSet<string>.Empty, []);

    /// <summary>Creates a snapshot. The validator and the service are the intended callers.</summary>
    public ManagedPolicySnapshot(
        long revision,
        ManagedPolicyReadStatus status,
        DateTimeOffset readAt,
        IReadOnlyDictionary<string, ManagedPolicyValue> enforcedValues,
        IReadOnlyDictionary<string, ManagedPolicyValue> recommendedValues,
        IReadOnlySet<string> invalidEnforcedKeys,
        IReadOnlyList<ManagedPolicyIssue> issues)
    {
        Revision = revision;
        Status = status;
        ReadAt = readAt;
        EnforcedValues = enforcedValues;
        RecommendedValues = recommendedValues;
        InvalidEnforcedKeys = invalidEnforcedKeys;
        Issues = issues;
    }

    /// <summary>Increases whenever published content changes.</summary>
    public long Revision { get; }

    /// <summary>Whether the source was readable and held any policy keys.</summary>
    public ManagedPolicyReadStatus Status { get; }

    /// <summary>When the source was read.</summary>
    public DateTimeOffset ReadAt { get; }

    /// <summary>Valid enforced values by canonical key name.</summary>
    public IReadOnlyDictionary<string, ManagedPolicyValue> EnforcedValues { get; }

    /// <summary>Valid recommended values by canonical key name.</summary>
    public IReadOnlyDictionary<string, ManagedPolicyValue> RecommendedValues { get; }

    /// <summary>Keys whose enforced value is present but invalid; their capabilities stay blocked.</summary>
    public IReadOnlySet<string> InvalidEnforcedKeys { get; }

    /// <summary>Validation findings for diagnostics.</summary>
    public IReadOnlyList<ManagedPolicyIssue> Issues { get; }

    /// <summary>Whether any policy applies, including invalid enforced keys.</summary>
    public bool IsManaged => EnforcedValues.Count > 0 || RecommendedValues.Count > 0 || InvalidEnforcedKeys.Count > 0;

    /// <summary>The valid enforced value of a key, or null.</summary>
    public ManagedPolicyValue? Enforced(string key) => EnforcedValues.GetValueOrDefault(key);

    /// <summary>The valid recommended value of a key, or null.</summary>
    public ManagedPolicyValue? Recommended(string key) => RecommendedValues.GetValueOrDefault(key);

    /// <summary>Whether a key has a valid enforced value.</summary>
    public bool IsEnforced(string key) => EnforcedValues.ContainsKey(key);

    /// <summary>Whether the capability behind a key must stay blocked: the source is unreadable or its enforced value is invalid.</summary>
    public bool IsBlocked(string key) => Status == ManagedPolicyReadStatus.Unreadable || InvalidEnforcedKeys.Contains(key);

    /// <summary>Resolves a boolean key by precedence. Check <see cref="IsBlocked"/> first.</summary>
    public ResolvedManagedSetting<bool> ResolveBoolean(string key, bool applicationDefault, bool? userPreference = null)
    {
        Expect(key, ManagedPolicyValueKind.Boolean);
        return ManagedSettingResolver.Resolve(applicationDefault,
            userPreference is { } user ? new(user) : null,
            Recommended(key) is { } recommended ? new(recommended.IsTrue) : null,
            Enforced(key) is { } enforced ? new(enforced.IsTrue) : null);
    }

    /// <summary>Resolves an enumeration key by precedence. Check <see cref="IsBlocked"/> first.</summary>
    public ResolvedManagedSetting<int> ResolveEnumeration(string key, int applicationDefault, int? userPreference = null)
    {
        Expect(key, ManagedPolicyValueKind.Enumeration);
        return ManagedSettingResolver.Resolve(applicationDefault,
            userPreference is { } user ? new(user) : null,
            Recommended(key) is { } recommended ? new(recommended.Number) : null,
            Enforced(key) is { } enforced ? new(enforced.Number) : null);
    }

    /// <summary>Resolves a text key by precedence. Check <see cref="IsBlocked"/> first.</summary>
    public ResolvedManagedSetting<string> ResolveText(string key, string applicationDefault, string? userPreference = null)
    {
        Expect(key, ManagedPolicyValueKind.Text);
        return ManagedSettingResolver.Resolve(applicationDefault,
            userPreference is { } user ? new(user) : null,
            Recommended(key) is { } recommended ? new(recommended.Text!) : null,
            Enforced(key) is { } enforced ? new(enforced.Text!) : null);
    }

    /// <summary>Keeps this snapshot's status, revision and issues but the values of an earlier readable snapshot.</summary>
    public ManagedPolicySnapshot RetainingValuesFrom(ManagedPolicySnapshot previous) =>
        new(Revision, Status, ReadAt, previous.EnforcedValues, previous.RecommendedValues, previous.InvalidEnforcedKeys, Issues);

    /// <summary>Compares everything except <see cref="Revision"/> and <see cref="ReadAt"/>.</summary>
    public bool ContentEquals(ManagedPolicySnapshot? other) =>
        other is not null && Status == other.Status &&
        SameValues(EnforcedValues, other.EnforcedValues) && SameValues(RecommendedValues, other.RecommendedValues) &&
        InvalidEnforcedKeys.SetEquals(other.InvalidEnforcedKeys) && Issues.SequenceEqual(other.Issues);

    private static bool SameValues(IReadOnlyDictionary<string, ManagedPolicyValue> left, IReadOnlyDictionary<string, ManagedPolicyValue> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && entry.Value.Equals(value));

    private static void Expect(string key, ManagedPolicyValueKind kind)
    {
        var definition = ManagedPolicyKeys.Find(key) ?? throw new ArgumentException($"Unknown managed policy key {key}.", nameof(key));
        if (definition.Kind != kind)
            throw new ArgumentException($"{key} is a {definition.Kind} key, not {kind}.", nameof(key));
    }
}
