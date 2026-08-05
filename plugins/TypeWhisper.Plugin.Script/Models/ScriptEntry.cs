namespace TypeWhisper.Plugin.Script;

/// <summary>Represents one persisted shell command.</summary>
public sealed record ScriptEntry
{
    /// <summary>Gets the stable identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Gets the display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets the shell command.</summary>
    public string Command { get; init; } = "";

    /// <summary>Gets the selected shell.</summary>
    public string Shell { get; init; } = ScriptShells.CommandPrompt;

    /// <summary>Gets whether this command participates in post-processing.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets the execution timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = ScriptDefaults.TimeoutSeconds;
}

internal static class ScriptDefaults
{
    internal const int TimeoutSeconds = 5;
    internal const int MinimumTimeoutSeconds = 1;
    internal const int MaximumTimeoutSeconds = 300;
}

internal static class ScriptShells
{
    internal const string CommandPrompt = "cmd";
    internal const string WindowsPowerShell = "powershell";
    internal const string PowerShell = "pwsh";

    internal static readonly IReadOnlyList<string> Supported =
        [CommandPrompt, WindowsPowerShell, PowerShell];

    internal static bool IsSupported(string? shell) =>
        Supported.Any(candidate => candidate.Equals(shell, StringComparison.OrdinalIgnoreCase));

    internal static string Normalize(string shell) =>
        Supported.FirstOrDefault(candidate => candidate.Equals(shell, StringComparison.OrdinalIgnoreCase)) ?? shell;
}
