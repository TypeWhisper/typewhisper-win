namespace TypeWhisper.Presentation;

/// <summary>
/// Recognizes a machine-wide installation: the per-machine MSI registers the folder it installed to under the
/// machine's uninstall key <c>MSI:&lt;package ID&gt;</c>. The running app belongs to it only when that folder is the
/// app's own installation root, so a per-user installation next to a machine-wide one keeps its own behavior.
/// </summary>
public static class MachineWideInstallation
{
    /// <summary>The uninstall key the per-machine MSI writes under HKLM for a package ID.</summary>
    public static string UninstallKey(string packageId) => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSI:" + packageId;

    /// <summary>Whether the registered install location is the running installation's root folder.</summary>
    public static bool Matches(string? registeredLocation, string? rootAppDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredLocation) || string.IsNullOrWhiteSpace(rootAppDirectory)) return false;
        try
        {
            static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return string.Equals(Normalize(registeredLocation), Normalize(rootAppDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
