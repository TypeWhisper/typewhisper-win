using System.Runtime.InteropServices;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class NativeMethodsTests
{
    [Fact]
    public void InputStruct_HasNativeWindowsSize()
    {
        var expectedSize = Environment.Is64BitProcess ? 40 : 28;

        Assert.Equal(expectedSize, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
