namespace TypeWhisper.Presentation;

/// <summary>Accesses a single explicitly named startup registration; tests supply an in-memory implementation.</summary>
public interface IStartupRegistrationBackend
{
    /// <summary>Reads the registered command, or null when no value exists.</summary>
    string? Read(string identity);
    /// <summary>Writes the exact command for this identity.</summary>
    void Write(string identity, string command);
    /// <summary>Deletes only this identity's value.</summary>
    void Delete(string identity);
}

/// <summary>The actual registration state and a visible explanation of unavailable or failed changes.</summary>
public sealed record StartupRegistrationState(bool IsEnabled, bool CanChange, string? Error);

/// <summary>Changes only an owned startup command and verifies the resulting backend state.</summary>
public sealed class StartupRegistration(IStartupRegistrationBackend backend, string identity, string? executable, string? unavailableReason = null)
{
    private string Command => QuoteCommand(executable!);

    /// <summary>Quotes a validated executable path and requests a silent tray launch.</summary>
    public static string QuoteCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Any(character => character == '"' || char.IsControl(character)))
            throw new ArgumentException("A fully qualified executable path without quotes or control characters is required.", nameof(executable));
        return "\"" + executable + "\" --minimized";
    }

    /// <summary>Reads actual state. Unavailable hosts, including test profiles, never access the backend.</summary>
    public StartupRegistrationState Read()
    {
        if (unavailableReason is not null || executable is null)
            return new(false, false, unavailableReason ?? "Startup registration is unavailable for this build.");
        try
        {
            var current = backend.Read(identity);
            if (current is null) return new(false, true, null);
            if (!string.Equals(current, Command, StringComparison.Ordinal))
                return new(false, false, "A different startup command uses this development identity. It was left unchanged.");
            return new(true, true, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, false, "Startup registration could not be read: " + ex.Message); }
    }

    /// <summary>Applies an explicit choice and reads it back; failed writes never report the requested state as saved.</summary>
    public StartupRegistrationState SetEnabled(bool enabled)
    {
        var before = Read();
        if (!before.CanChange || before.IsEnabled == enabled) return before;
        string? failure = null;
        try { if (enabled) backend.Write(identity, Command); else backend.Delete(identity); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = "Startup registration could not be changed: " + ex.Message; }
        var actual = Read();
        return actual with { Error = failure ?? actual.Error ?? (actual.IsEnabled == enabled ? null : "Windows did not save the requested startup registration.") };
    }
}
