using System.Reflection;
using TypeWhisper.WinUI.Platform;
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

    [Theory]
    [InlineData(NativeMethods.CF_DIB, new[] { NativeMethods.CF_BITMAP }, true)]
    [InlineData(NativeMethods.CF_BITMAP, new[] { NativeMethods.CF_DIBV5 }, true)]
    [InlineData(NativeMethods.CF_DIBV5, new[] { NativeMethods.CF_DIB }, false)]
    [InlineData(NativeMethods.CF_UNICODETEXT, new[] { NativeMethods.CF_TEXT }, false)]
    [InlineData(NativeMethods.CF_ENHMETAFILE, new[] { NativeMethods.CF_METAFILEPICT }, false)]
    [InlineData(NativeMethods.CF_TEXT, new[] { NativeMethods.CF_UNICODETEXT, NativeMethods.CF_LOCALE }, true)]
    [InlineData(NativeMethods.CF_METAFILEPICT, new[] { NativeMethods.CF_ENHMETAFILE }, true)]
    [InlineData(NativeMethods.CF_DIB, new[] { NativeMethods.CF_UNICODETEXT }, false)]
    [InlineData(NativeMethods.CF_DIB, new uint[0], false)]
    [InlineData(NativeMethods.CF_HDROP, new[] { NativeMethods.CF_BITMAP }, false)]
    public void OnlyFormatsWindowsCanSynthesizeFromCapturedDataMayBeSkipped(uint unavailable, uint[] captured, bool expected)
    {
        var policy = typeof(WindowsClipboardTransaction).GetMethod("CanSkipSynthesizedFormat", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, (bool)policy.Invoke(null, [unavailable, captured.ToHashSet()])!);
    }
}
