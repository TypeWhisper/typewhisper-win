using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace TypeWhisper.Presentation;

/// <summary>A PowerShell whose current-user profile can load the CLI completion.</summary>
public enum PowerShellEdition
{
    /// <summary>Windows PowerShell 5.1, profile under Documents\WindowsPowerShell.</summary>
    Windows,
    /// <summary>PowerShell 7, profile under Documents\PowerShell.</summary>
    Core
}

/// <summary>What setting up the PowerShell completion changed.</summary>
/// <param name="Added">Profiles that now load the completion.</param>
/// <param name="BlockedByPolicy">Installed editions without a profile whose execution policy would refuse a new one.</param>
/// <param name="Error">Why a profile could not be changed, if one could not.</param>
public sealed record PowerShellCompletionSetup(IReadOnlyList<string> Added, IReadOnlyList<PowerShellEdition> BlockedByPolicy, string? Error);

/// <summary>
/// Adds the CLI's tab completion to the current user's PowerShell profiles and removes it again. Each install
/// directory owns one marked block, so other profile content and other TypeWhisper installations stay untouched.
/// </summary>
public sealed class PowerShellCompletionProfiles
{
    private const string BeginPrefix = "# >>> TypeWhisper CLI completion: ";
    private const string End = "# <<< TypeWhisper CLI completion <<<";
    private readonly string _documents;
    private readonly Func<PowerShellEdition, bool> _isInstalled;
    private readonly Func<PowerShellEdition, bool> _allowsProfileScripts;

    /// <summary>Uses the current user's Documents folder, installed PowerShells and execution policies.</summary>
    public PowerShellCompletionProfiles() : this(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), IsInstalled, AllowsProfileScripts) { }

    /// <summary>Uses an explicit Documents folder and PowerShell probes for isolated operation and testing.</summary>
    public PowerShellCompletionProfiles(string documentsDirectory, Func<PowerShellEdition, bool> isInstalled, Func<PowerShellEdition, bool> allowsProfileScripts)
    {
        _documents = documentsDirectory;
        _isInstalled = isInstalled;
        _allowsProfileScripts = allowsProfileScripts;
    }

    /// <summary>The all-hosts profile of <paramref name="edition"/>, which PowerShell loads in consoles, terminals and editors.</summary>
    public string ProfilePath(PowerShellEdition edition) =>
        Path.Join(_documents, edition == PowerShellEdition.Windows ? "WindowsPowerShell" : "PowerShell", "profile.ps1");

    /// <summary>Adds or refreshes the completion block for <paramref name="executable"/> in every installed edition's profile.</summary>
    public PowerShellCompletionSetup Register(string executable)
    {
        var added = new List<string>();
        var blocked = new List<PowerShellEdition>();
        string? error = null;
        if (string.IsNullOrWhiteSpace(_documents)) return new(added, blocked, null);
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        foreach (var (edition, profile) in Enum.GetValues<PowerShellEdition>().Select(edition => (edition, ProfilePath(edition))))
        {
            try
            {
                // A new profile under a policy that refuses scripts would only print an error in every session.
                if (!File.Exists(profile) && (!_isInstalled(edition) || !_allowsProfileScripts(edition)))
                {
                    if (_isInstalled(edition)) blocked.Add(edition);
                    continue;
                }
                var (text, encoding) = Read(profile);
                // Appending would void the signature, and AllSigned would then refuse the whole profile.
                if (IsSigned(text))
                {
                    error ??= Loc.T("The PowerShell profile {0} is signed. Add the completion line yourself and sign the profile again.", profile);
                    continue;
                }
                // Rewriting would drop the mark of the web and let RemoteSigned run a downloaded profile.
                if (IsFromInternet(profile))
                {
                    error ??= Loc.T("The PowerShell profile {0} was downloaded from the internet, so PowerShell blocks it. Check and unblock it, then select Update.", profile);
                    continue;
                }
                text = WithoutBlock(text, directory);
                if (text.Length > 0 && !text.EndsWith('\n')) text += "\r\n";
                Write(profile, text + Block(executable, directory), encoding);
                added.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error ??= ex.Message;
            }
        }
        return new(added, blocked, error);
    }

    /// <summary>Removes the completion block of <paramref name="installDirectory"/> from both editions' profiles.</summary>
    /// <returns>Why a profile could not be changed, or null.</returns>
    public string? Unregister(string installDirectory)
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(_documents)) return null;
        var directory = Path.GetFullPath(installDirectory);
        foreach (var profile in Enum.GetValues<PowerShellEdition>().Select(ProfilePath))
        {
            try
            {
                if (!File.Exists(profile)) continue;
                var (text, encoding) = Read(profile);
                // A block left in a signed or blocked profile stays silent once the CLI is gone.
                if (IsSigned(text) || IsFromInternet(profile)) continue;
                var remaining = WithoutBlock(text, directory);
                if (remaining == text) continue;
                // A profile that only held the completion was created for it and goes with it.
                if (string.IsNullOrWhiteSpace(remaining)) File.Delete(profile);
                else Write(profile, remaining, encoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error ??= ex.Message;
            }
        }
        return error;
    }

    // The Zone.Identifier stream marks downloads; Internet (3) and Restricted (4) profiles are blocked by RemoteSigned.
    private static bool IsFromInternet(string profile)
    {
        var stream = profile + ":Zone.Identifier";
        if (!File.Exists(stream)) return false;
        try
        {
            var zone = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(stream), @"(?m)^\s*ZoneId\s*=\s*(\d+)");
            return !zone.Success || int.Parse(zone.Groups[1].Value) >= 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool IsSigned(string text) => text.Contains("# SIG # Begin signature block", StringComparison.OrdinalIgnoreCase);

    // The block stays ASCII: Windows PowerShell 5.1 reads a profile without byte order mark in the ANSI code page.
    private static string Begin(string directory) =>
        BeginPrefix + string.Concat(Path.TrimEndingDirectorySeparator(directory).Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:X4}")) + " >>>";

    // A path with non-ASCII characters, such as from the user name, is rebuilt from character codes.
    private static string PowerShellString(string value) => value.All(c => c < 128)
        ? "'" + value.Replace("'", "''") + "'"
        : $"([string]::new([char[]]@({string.Join(',', value.Select(c => (int)c))})))";

    private static string Block(string executable, string directory)
    {
        var path = PowerShellString(Path.GetFullPath(executable));
        return $"{Begin(directory)}\r\nif (Test-Path -LiteralPath {path}) {{ & {path} completions powershell | Out-String | Invoke-Expression }}\r\n{End}\r\n";
    }

    // Removes every complete block of this directory. A block is complete only when its end marker comes
    // before the next block starts; an edited block is left for the user rather than taking a neighbor with it.
    private static string WithoutBlock(string text, string directory)
    {
        var begin = Begin(directory);
        var result = new StringBuilder(text.Length);
        var kept = 0;
        for (var start = text.IndexOf(begin, StringComparison.OrdinalIgnoreCase); start >= 0;
            start = text.IndexOf(begin, start, StringComparison.OrdinalIgnoreCase))
        {
            var end = text.IndexOf(End, start, StringComparison.OrdinalIgnoreCase);
            var next = text.IndexOf(BeginPrefix, start + begin.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0 || next >= 0 && next < end)
            {
                start += begin.Length;
                continue;
            }
            end += End.Length;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
            result.Append(text, kept, start - kept);
            kept = start = end;
        }
        return result.Append(text, kept, text.Length - kept).ToString();
    }

    // Keeps the profile's bytes: Unicode profiles by their byte order mark, anything else as Latin-1,
    // which maps every byte to one character and back. The block itself is ASCII.
    private static (string Text, Encoding Encoding) Read(string path)
    {
        if (!File.Exists(path)) return ("", new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(path);
        Encoding encoding = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true),
            [0xFF, 0xFE, ..] => new UnicodeEncoding(false, true),
            [0xFE, 0xFF, ..] => new UnicodeEncoding(true, true),
            _ => Encoding.Latin1
        };
        var preamble = encoding.GetPreamble().Length;
        return (encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
    }

    private static void Write(string path, string text, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Replacing the file drops its alternate data streams; keep the zone information it had.
        var zoneStream = path + ":Zone.Identifier";
        var zone = File.Exists(zoneStream) ? File.ReadAllBytes(zoneStream) : null;
        var temporary = Path.Join(Path.GetDirectoryName(path), $".typewhisper-profile-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (zone is not null) File.WriteAllBytes(zoneStream, zone);
    }

    private static bool IsInstalled(PowerShellEdition edition)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (edition == PowerShellEdition.Windows)
            return File.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
        return PowerShellCoreExecutables().Any(File.Exists)
            || File.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe"))
            || Directory.Exists(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell"));
    }

    // Installed pwsh.exe files whose directory is $PSHOME. The Store app's alias has no readable $PSHOME.
    private static IEnumerable<string> PowerShellCoreExecutables() =>
        new[] { Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Join(directory.Trim('"'), "pwsh.exe")))
            .Where(path => !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase));

    // Follows PowerShell's precedence: machine policy, user policy, current user, local machine, default.
    // The process scope does not apply to new sessions.
    private static bool AllowsProfileScripts(PowerShellEdition edition)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var policyKey = edition == PowerShellEdition.Windows ? @"Software\Policies\Microsoft\Windows\PowerShell" : @"Software\Policies\Microsoft\PowerShellCore";
            var policy = GroupPolicy(Registry.LocalMachine, policyKey) ?? GroupPolicy(Registry.CurrentUser, policyKey);
            if (edition == PowerShellEdition.Windows)
            {
                const string shell = @"Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";
                policy ??= Setting(Registry.CurrentUser, shell) ?? Setting(Registry.LocalMachine, shell) ?? "Restricted";
            }
            // PowerShell 7 keeps the current-user and local-machine scopes in powershell.config.json files.
            else policy ??= ConfigSetting(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell"))
                ?? PowerShellCoreExecutables().Where(File.Exists).Select(path => ConfigSetting(Path.GetDirectoryName(path)!)).FirstOrDefault(value => value is not null)
                ?? "RemoteSigned";
            return policy.Equals("RemoteSigned", StringComparison.OrdinalIgnoreCase)
                || policy.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase)
                || policy.Equals("Bypass", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static string? GroupPolicy(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        if (key?.GetValue("EnableScripts") is not int enabled) return null;
        return enabled == 0 ? "Restricted" : key.GetValue("ExecutionPolicy") as string;
    }

    [SupportedOSPlatform("windows")]
    private static string? Setting(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue("ExecutionPolicy") is string value && !value.Equals("Undefined", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static string? ConfigSetting(string directory)
    {
        try
        {
            var path = Path.Join(directory, "powershell.config.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Microsoft.PowerShell:ExecutionPolicy", out var value) && value.ValueKind == JsonValueKind.String
                && !string.Equals(value.GetString(), "Undefined", StringComparison.OrdinalIgnoreCase) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
