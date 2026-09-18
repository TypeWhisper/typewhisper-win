namespace TypeWhisper.PluginSystem.Tests;

/// <summary>Runs actual Media Foundation encoding only where the native Windows codec exists.</summary>
public sealed class WindowsMediaFoundationFactAttribute : FactAttribute
{
    public WindowsMediaFoundationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Media Foundation AAC encoding requires Windows; portable protocol tests run on every platform.";
    }
}
