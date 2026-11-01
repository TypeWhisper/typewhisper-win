using TypeWhisper.WinUI.Platform;
using Xunit;

public sealed class SyntheticKeysTests
{
    // Thread 0 uses the test thread's own layout. Ctrl and Enter have the same scan code on
    // every layout, so the expectations do not depend on the machine.
    private static readonly SyntheticKeys Keys = new(0);

    [Fact]
    public void PasteKeysCarryDistinctScanCodesLikeARealKeyboard()
    {
        var ctrl = Keys.Down(0x11).Data.Keyboard;
        var v = Keys.Down(0x56).Data.Keyboard;

        Assert.Equal(0x1D, ctrl.Scan);
        Assert.NotEqual(0, v.Scan);
        Assert.NotEqual(ctrl.Scan, v.Scan);
        Assert.Equal(0u, ctrl.Flags);
    }

    [Fact]
    public void ReleaseKeepsTheScanCodeAndOurMarker()
    {
        var input = Keys.Up(0x0D);

        Assert.Equal(NativeMethods.INPUT_KEYBOARD, input.Type);
        Assert.Equal(0x0D, input.Data.Keyboard.Key);
        Assert.Equal(0x1C, input.Data.Keyboard.Scan);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, input.Data.Keyboard.Flags);
        Assert.True(OwnKeyboardInput.Sent(0x10, input.Data.Keyboard.Extra));
    }

    [Fact]
    public void ExtendedKeysGetTheExtendedFlag()
    {
        var rightCtrl = Keys.Down(0xA3).Data.Keyboard;

        Assert.Equal(0x1D, rightCtrl.Scan);
        Assert.Equal(NativeMethods.KEYEVENTF_EXTENDEDKEY, rightCtrl.Flags);
    }
}
