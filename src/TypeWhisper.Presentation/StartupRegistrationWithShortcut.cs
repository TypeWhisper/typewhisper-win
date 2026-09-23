namespace TypeWhisper.Presentation;

/// <summary>Preserves an existing owned 1.0 startup shortcut while using the current registry backend for new registrations.</summary>
public sealed class StartupRegistrationWithShortcut(IStartupRegistration registry, Func<bool> hasShortcut, Action removeShortcut) : IStartupRegistration
{
    /// <inheritdoc />
    public async Task<StartupRegistrationState> ReadAsync()
    {
        var state = await registry.ReadAsync();
        try { return state with { IsEnabled = state.IsEnabled || hasShortcut() }; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return state with { CanChange = false, Error = "The existing startup shortcut could not be read. " + ex.Message }; }
    }

    /// <inheritdoc />
    public async Task<StartupRegistrationState> SetEnabledAsync(bool enabled)
    {
        var before = await ReadAsync();
        if (!before.CanChange || before.IsEnabled == enabled) return before;
        try
        {
            if (!enabled) removeShortcut();
            var result = await registry.SetEnabledAsync(enabled);
            var actual = await ReadAsync();
            return actual with { Error = result.Error ?? actual.Error };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return (await ReadAsync()) with { Error = "Startup could not be changed. " + ex.Message }; }
    }
}
