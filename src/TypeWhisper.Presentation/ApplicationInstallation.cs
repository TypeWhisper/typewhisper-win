namespace TypeWhisper.Presentation;

/// <summary>Keeps the original installation and the side-by-side Daily on their own package identities.</summary>
public sealed record ApplicationInstallation(string PackageId, string Executable)
{
    /// <summary>The original installation retains its executable name for existing shortcuts and startup entries.</summary>
    public static ApplicationInstallation? Resolve(string? packageId) => packageId switch
    {
        "TypeWhisper" => new("TypeWhisper", "TypeWhisper.exe"),
        "TypeWhisperDaily" => new("TypeWhisperDaily", "TypeWhisper.exe"),
        _ => null
    };

    /// <summary>Points an exact owned early 1.1 Daily command at the renamed executable; never creates a registration.</summary>
    public void MigrateStartupCommand(IStartupRegistrationBackend backend, string executable)
    {
        if (PackageId == "TypeWhisperDaily")
            StartupRegistration.MigrateExecutable(backend, PackageId, Path.Combine(Path.GetDirectoryName(executable)!, "TypeWhisper.WinUI.exe"), executable);
    }

    /// <summary>Only the original Daily feed changes generations. Stable and RC never fall back to WPF.</summary>
    public string Feed(AppUpdateChannel channel, string architecture) => PackageId == "TypeWhisper" && channel == AppUpdateChannel.Daily
        ? architecture is "win-x64" or "win-arm64" ? architecture + "-daily" : throw new ArgumentException("Unsupported architecture.")
        : AppUpdatePreferences.Feed(channel, architecture);

    /// <summary>Rejects cross-installation updates and packages from the previous application generation.</summary>
    public bool Accepts(string packageId, int majorVersion, int minorVersion) =>
        packageId == PackageId && (majorVersion > 1 || majorVersion == 1 && minorVersion >= 1);
}
