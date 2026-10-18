using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Resolves validated managed preferences without reading or writing user settings.</summary>
/// <remarks>
/// This is only the precedence layer for preferences that support recommendations.
/// Policy parsing, validation, restriction intersection and runtime enforcement belong to
/// separate layers. An invalid or unreadable enforced policy must not be passed as absent.
/// </remarks>
public static class ManagedSettingResolver
{
    /// <summary>Selects enforced policy, explicit user choice, recommendation, then application default.</summary>
    /// <typeparam name="T">The setting's value type.</typeparam>
    /// <param name="applicationDefault">The built-in fallback value.</param>
    /// <param name="explicitUserPreference">The user's explicit choice, or null if never set.</param>
    /// <param name="recommendedPolicy">A validated recommendation, or null if absent.</param>
    /// <param name="enforcedPolicy">A validated mandatory setting, or null if confirmed absent.</param>
    /// <returns>The effective value and its source. Do not persist it over the user's choice.</returns>
    public static ResolvedManagedSetting<T> Resolve<T>(
        T applicationDefault,
        ManagedSettingValue<T>? explicitUserPreference = null,
        ManagedSettingValue<T>? recommendedPolicy = null,
        ManagedSettingValue<T>? enforcedPolicy = null) where T : notnull
    {
        if (enforcedPolicy is not null)
            return new(enforcedPolicy.Value, ManagedSettingSource.EnforcedPolicy);

        if (explicitUserPreference is not null)
            return new(explicitUserPreference.Value, ManagedSettingSource.UserPreference);

        if (recommendedPolicy is not null)
            return new(recommendedPolicy.Value, ManagedSettingSource.RecommendedPolicy);

        return new(applicationDefault, ManagedSettingSource.ApplicationDefault);
    }
}
