namespace TypeWhisper.Core.Models;

/// <summary>Identifies the source of an effective managed preference.</summary>
public enum ManagedSettingSource
{
    /// <summary>The application's built-in default.</summary>
    ApplicationDefault,
    /// <summary>An editable administrator recommendation.</summary>
    RecommendedPolicy,
    /// <summary>A value explicitly selected by the user, including a factory-default value.</summary>
    UserPreference,
    /// <summary>A mandatory administrator setting.</summary>
    EnforcedPolicy
}

/// <summary>
/// Represents a present, validated setting. A null wrapper means the source has no value;
/// values such as false or zero are still explicit choices.
/// </summary>
/// <param name="Value">The validated value supplied by this source.</param>
/// <typeparam name="T">The setting's value type.</typeparam>
public sealed record ManagedSettingValue<T>(T Value) where T : notnull;

/// <summary>An effective preference for consumption, separate from stored user settings.</summary>
/// <param name="Value">The value selected by precedence.</param>
/// <param name="Source">The source that supplied the value.</param>
/// <typeparam name="T">The setting's value type.</typeparam>
public sealed record ResolvedManagedSetting<T>(T Value, ManagedSettingSource Source) where T : notnull
{
    /// <summary>Whether the effective setting is fixed by an administrator.</summary>
    public bool IsEnforced => Source == ManagedSettingSource.EnforcedPolicy;
}
