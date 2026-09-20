namespace TypeWhisper.Presentation;

/// <summary>Package startup states, including choices that an application must not override.</summary>
public enum PackagedStartupState
{
    /// <summary>Disabled; the app may request enablement.</summary>
    Disabled,
    /// <summary>Disabled in Windows settings; only the user can re-enable it there.</summary>
    DisabledByUser,
    /// <summary>Enabled; the app may disable it.</summary>
    Enabled,
    /// <summary>Disabled by policy; the app cannot change it.</summary>
    DisabledByPolicy,
    /// <summary>Enabled by policy; the app cannot change it.</summary>
    EnabledByPolicy
}

/// <summary>Controls a manifest-declared startup task without writing registry commands.</summary>
public sealed class PackagedStartupRegistration(
    Func<Task<PackagedStartupState>> read,
    Func<Task> enable,
    Func<Task> disable) : IStartupRegistration
{
    /// <inheritdoc />
    public async Task<StartupRegistrationState> ReadAsync()
    {
        try
        {
            return (await read()) switch
            {
                PackagedStartupState.Disabled => new(false, true, null),
                PackagedStartupState.Enabled => new(true, true, null),
                PackagedStartupState.DisabledByUser => new(false, false, "Startup was disabled in Windows. Enable TypeWhisper in Settings > Apps > Startup."),
                PackagedStartupState.DisabledByPolicy => new(false, false, "Startup is disabled by your organization's policy."),
                PackagedStartupState.EnabledByPolicy => new(true, false, "Startup is enabled by your organization's policy."),
                _ => new(false, false, "Windows returned an unsupported startup state.")
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, false, "Startup registration could not be read: " + ex.Message); }
    }

    /// <inheritdoc />
    public async Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
    {
        var before = await ReadAsync();
        if (!before.CanChange || before.IsEnabled == enabled) return before;
        string? failure = null;
        try { await (enabled ? enable() : disable()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { failure = "Startup registration could not be changed: " + ex.Message; }
        var actual = await ReadAsync();
        return actual with { Error = failure ?? actual.Error ?? (actual.IsEnabled == enabled ? null : "Windows did not save the requested startup registration.") };
    }
}
