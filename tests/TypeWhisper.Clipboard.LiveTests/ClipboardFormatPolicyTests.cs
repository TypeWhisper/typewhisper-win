using System.Reflection;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using Xunit;

public sealed class ClipboardFormatPolicyTests
{
    // No clipboard reads/writes: registering a format only resolves its ID.
    [Theory]
    [InlineData("FileContents", true, true)]
    [InlineData("FileContents", false, false)]
    [InlineData("System.Drawing.Bitmap", true, false)]
    [InlineData("TypeWhisper.Tests.Unknown", true, false)]
    [InlineData("FileName", true, false)]
    public void OnlyRedundantFileContentsMayBeSkipped(string name, bool capturedFiles, bool expected)
    {
        var format = NativeMethods.RegisterClipboardFormat(name);
        Assert.NotEqual(0u, format);
        var policy = typeof(WindowsClipboardTransaction).GetMethod("CanSkipUnavailableFormat", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (bool)policy.Invoke(null, [format, capturedFiles])!);
    }
}
