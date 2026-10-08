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

public sealed class LiveClaudeFactAttribute : FactAttribute
{
    public LiveClaudeFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "CLI process isolation requires Windows.";
        else if (Environment.GetEnvironmentVariable("TYPEWHISPER_LIVE_CLAUDE_TEST") != "1")
            Skip = "Set TYPEWHISPER_LIVE_CLAUDE_TEST=1 to run the authenticated Claude package test.";
    }
}
