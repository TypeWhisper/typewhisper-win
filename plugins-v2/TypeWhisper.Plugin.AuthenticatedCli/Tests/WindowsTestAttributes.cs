namespace TypeWhisper.PluginSystem.Tests;

// The native runner intentionally uses Windows job objects and executable discovery.
// Keep profile, catalog and portable-host contract tests enabled on every CI platform.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows CLI fixtures or directory junctions.";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires native Windows CLI fixtures or directory junctions.";
    }
}
