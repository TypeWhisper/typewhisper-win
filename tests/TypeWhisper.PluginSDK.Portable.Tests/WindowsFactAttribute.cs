using Xunit;

// Windows refuses to replace a file that another handle has open; other platforms replace it anyway, so these tests
// only describe Windows behaviour.
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows file-sharing behaviour.";
    }
}
