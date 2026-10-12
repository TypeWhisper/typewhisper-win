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

    /// <summary>The separate early 1.1 Daily installation only receives Daily builds; Stable and RC ship as the original installation.</summary>
    public bool Supports(AppUpdateChannel channel) => PackageId == "TypeWhisper" || channel == AppUpdateChannel.Daily;

    /// <summary>
    /// The original installation keeps the 1.0 feeds on every channel, so 1.0 installations upgrade to 1.1 in place.
    /// <see cref="Accepts"/> keeps 1.1 from installing the 1.0 packages those feeds still hold.
    /// </summary>
    public string Feed(AppUpdateChannel channel, string architecture)
    {
        if (architecture is not ("win-x64" or "win-arm64")) throw new ArgumentException("Unsupported architecture.", nameof(architecture));
        if (!Enum.IsDefined(channel) || !Supports(channel)) throw new ArgumentOutOfRangeException(nameof(channel));
        if (PackageId == "TypeWhisperDaily") return architecture + "-winui-daily";
        return channel switch
        {
            AppUpdateChannel.Stable => architecture,
            AppUpdateChannel.ReleaseCandidate => architecture + "-rc",
            _ => architecture + "-daily"
        };
    }

    /// <summary>Rejects cross-installation updates and packages from the previous application generation.</summary>
    public bool Accepts(string packageId, int majorVersion, int minorVersion) =>
        packageId == PackageId && (majorVersion > 1 || majorVersion == 1 && minorVersion >= 1);
}
