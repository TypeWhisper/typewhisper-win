using Xunit;

namespace TypeWhisper.Presentation.Tests;

/// <summary>A test that calls Windows APIs, such as process tokens or the Windows folder, and is skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows.";
    }
}
