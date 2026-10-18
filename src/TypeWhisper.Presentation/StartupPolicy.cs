using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>What the startup policy remembers across launches, kept apart from the registration itself.</summary>
/// <param name="UserChoice">The user's last explicit startup choice, or null when never changed in this profile.</param>
/// <param name="RetainedBeforeEnforcement">The registration before an enforced value replaced it; restored once the policy is removed.</param>
/// <param name="RecommendationApplied">Whether an administrator recommendation was applied to this new profile already.</param>
public sealed record StartupPolicyPreferences(bool? UserChoice = null, bool? RetainedBeforeEnforcement = null, bool RecommendationApplied = false);

/// <summary>Atomically persists <see cref="StartupPolicyPreferences"/>; an unreadable file behaves like an empty one.</summary>
public sealed class StartupPolicyStore
{
    private readonly string _path;

    /// <summary>The loaded or last saved preferences.</summary>
    public StartupPolicyPreferences Current { get; private set; } = new();

    /// <summary>A readable persistence failure.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads the preferences without creating files.</summary>
    public StartupPolicyStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Current = JsonSerializer.Deserialize<StartupPolicyPreferences>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = Loc.T("The startup policy state could not be loaded."); }
    }

    /// <summary>Saves atomically; <see cref="Current"/> advances only once the file holds the new state.</summary>
    public bool Save(StartupPolicyPreferences next)
    {
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(next));
            Current = next;
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Loc.T("The startup policy state could not be saved.");
            return false;
        }
    }
}

/// <summary>The registration after policy was applied and whether users may change it.</summary>
/// <param name="State">The actual registration state.</param>
/// <param name="Locked">True while policy owns the setting.</param>
/// <param name="LockReason">What the settings row shows while locked.</param>
/// <param name="Notice">A persistence problem the settings row shows, or null.</param>
public sealed record StartupPolicyOutcome(StartupRegistrationState State, bool Locked, string? LockReason, string? Notice = null);

/// <summary>Keeps the owned startup registration in line with <c>LaunchAtLogin</c> without losing the user's own choice.</summary>
public sealed class StartupPolicyReconciler(IStartupRegistration registration, StartupPolicyStore store)
{
    /// <summary>The persisted policy state.</summary>
    public StartupPolicyStore Store => store;

    /// <summary>
    /// Applies an enforced value, restores the retained registration once enforcement is removed, and applies a
    /// recommendation only to a new profile without an explicit choice. An unreadable policy locks the setting as is.
    /// </summary>
    public async Task<StartupPolicyOutcome> ApplyAsync(ManagedPolicySnapshot snapshot, bool setupCompleted)
    {
        var state = await registration.ReadAsync();
        if (snapshot.IsBlocked(ManagedPolicyKeys.LaunchAtLogin))
            return new(state, true, Loc.T("Your organization's policy could not be read. This setting waits until it is readable."));

        if (snapshot.Enforced(ManagedPolicyKeys.LaunchAtLogin) is { } enforced)
        {
            // The pre-enforcement value must be on disk before the registration changes; otherwise a restart under
            // the same policy would retain the enforced state and a later removal could not restore the user's choice.
            // An unreadable state file may already hold the pre-enforcement value: nothing is written over it.
            if (store.Current.RetainedBeforeEnforcement is null && !state.Unknown
                && (store.Error is not null || !store.Save(store.Current with { RetainedBeforeEnforcement = state.IsEnabled })))
                return new(state, true, store.Error ?? Loc.T("Managed by your organization"));
            if (state.CanChange && state.IsEnabled != enforced.IsTrue)
                state = await registration.SetEnabledAsync(enforced.IsTrue);
            return new(state, true, Loc.T("Managed by your organization"));
        }

        if (store.Current.RetainedBeforeEnforcement is { } retained)
        {
            // Confirmed removal restores the registration the user had before enforcement. The retained value is
            // cleared only once the registration matches it, so a temporary restriction keeps it for a later pass.
            if (state.CanChange && state.IsEnabled != retained)
                state = await registration.SetEnabledAsync(retained);
            // A clear that did not reach the disk is reported: the next launch would otherwise restore it again.
            if (state.Unknown || state.IsEnabled != retained) return new(state, false, null);
            if (!store.Save(store.Current with { RetainedBeforeEnforcement = null })) return new(state, false, null, store.Error);
            // A recommendation that replaced the enforcement in the same revision is evaluated by this pass.
        }

        // A recommendation needs a readable preference state: an unreadable file may hold the user's own choice.
        if (snapshot.Recommended(ManagedPolicyKeys.LaunchAtLogin) is { } recommended && store.Error is null
            && store.Current.UserChoice is null && !setupCompleted && !store.Current.RecommendationApplied)
        {
            if (state.CanChange && state.IsEnabled != recommended.IsTrue)
                state = await registration.SetEnabledAsync(recommended.IsTrue);
            // The recommendation is final once the registration matches it, or once Windows itself keeps the app from
            // changing it (a user turned startup off in Settings). An unknown registration or a failed write is
            // retried on the next pass.
            if ((state.IsEnabled == recommended.IsTrue || (!state.CanChange && !state.Unknown))
                && !store.Save(store.Current with { RecommendationApplied = true }))
                return new(state, false, null, store.Error);
        }
        return new(state, false, null);
    }

    /// <summary>Records an explicit user choice so recommendations no longer apply to this profile; false when it could not be saved.</summary>
    public bool RecordUserChoice(bool enabled) => store.Save(store.Current with { UserChoice = enabled });
}
