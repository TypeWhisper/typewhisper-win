using System.IO;

namespace TypeWhisper.Plugin.Script;

/// <summary>
/// Resolves script shells to absolute paths. A bare name such as "cmd.exe" lets CreateProcess
/// search the application and working directories first, both of which are user-writable in a
/// per-user installation, so a planted binary could replace the shell.
/// </summary>
internal static class ScriptShellLocator
{
    internal const string PowerShellCoreFileName = "pwsh.exe";
    private static readonly Lazy<string?> s_powerShellCore = new(() => FindPowerShellCore(
        Environment.GetEnvironmentVariable("PATH"),
        [AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath), Environment.CurrentDirectory],
        File.Exists));

    /// <summary>The 64-bit Command Prompt in the Windows system directory.</summary>
    internal static string CommandPrompt => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Windows PowerShell 5.1 in the Windows system directory.</summary>
    internal static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>PowerShell 7 from PATH, resolved once per process; null when it is not installed.</summary>
    internal static string? PowerShellCore => s_powerShellCore.Value;

    internal static string? Resolve(string shell) => shell switch
    {
        ScriptShells.WindowsPowerShell => WindowsPowerShell,
        ScriptShells.PowerShell => PowerShellCore,
        _ => CommandPrompt
    };

    /// <summary>
    /// Walks PATH for pwsh.exe. Relative entries and the excluded directories are skipped because
    /// they resolve against locations the current user can write to.
    /// </summary>
    internal static string? FindPowerShellCore(string? path, IEnumerable<string?> excludedDirectories, Func<string, bool> fileExists)
    {
        var excluded = excludedDirectories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => NormalizeDirectory(directory!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory)) continue;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, PowerShellCoreFileName)); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { continue; }
            if (excluded.Contains(NormalizeDirectory(Path.GetDirectoryName(candidate)!))) continue;
            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    private static string NormalizeDirectory(string directory)
    {
        try { return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return directory; }
    }
}
