namespace TypeWhisper.PluginSystem.Tests;

// The local runtime intentionally uses Windows job objects and Windows paths.
// Keep asset, catalog and portable-host contract tests enabled on every CI platform.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) Skip = "Requires Windows x64 runtime, path or process fixtures.";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) Skip = "Requires Windows x64 runtime, path or process fixtures.";
    }
}
