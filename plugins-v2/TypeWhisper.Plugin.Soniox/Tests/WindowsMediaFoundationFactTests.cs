using System.Runtime.InteropServices;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WindowsMediaFoundationFactTests
{
    [Fact]
    public void NonWindowsDoesNotProbeNativeCode() =>
        Assert.NotNull(WindowsMediaFoundationFactAttribute.GetSkipReason(false, () => throw new InvalidOperationException()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsRequiresAvailableEncoder(bool available) =>
        Assert.Equal(available, WindowsMediaFoundationFactAttribute.GetSkipReason(true, () => available) is null);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingNativeComponentsAreSkipped(int kind)
    {
        Exception failure = kind switch
        {
            0 => new DllNotFoundException(),
            1 => new EntryPointNotFoundException(),
            2 => new COMException(),
            _ => new TypeInitializationException("MediaFoundation", new DllNotFoundException())
        };
        Assert.NotNull(WindowsMediaFoundationFactAttribute.GetSkipReason(true, () => throw failure));
    }

    [Fact]
    public void UnexpectedProbeFailureIsNotHidden() =>
        Assert.Throws<InvalidOperationException>(() => WindowsMediaFoundationFactAttribute.GetSkipReason(true,
            () => throw new InvalidOperationException()));
}
