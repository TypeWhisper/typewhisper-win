using TypeWhisper.WinUI;
using Xunit;

public class ShortcutTestSessionTests
{
    private const int Ctrl = 0xA2, Shift = 0xA0, F2 = 0x71, Backslash = 0xDC, BrightnessLikeMediaKey = 0xAF;

    [Fact]
    public void ShortcutThatReachesItsOwnerWorks()
    {
        var test = new ShortcutTestSession(["Ctrl+\\"], 0);
        test.Delivered(Ctrl, true, 100);
        test.Delivered(Backslash, true, 120);
        Assert.Equal(ShortcutTestVerdict.Waiting, test.Verdict(130, paused: false));
        test.Respond("CTRL+\\");
        Assert.Equal(ShortcutTestVerdict.Works, test.Verdict(140, paused: false));
        Assert.Equal("CTRL+\\", test.Responded);
    }

    [Fact]
    public void ModifierOnlyShortcutIsRecognizedWhenItsLastModifierIsPressed()
    {
        var test = new ShortcutTestSession(["Ctrl+Shift"], 0);
        test.Delivered(Ctrl, true, 100);
        Assert.Null(test.Seen);
        test.Delivered(Shift, true, 150);
        Assert.Equal("CTRL+SHIFT", test.Seen);
        Assert.Equal(ShortcutTestVerdict.NotResponding, test.Verdict(150 + ShortcutTestSession.ResponseMilliseconds, paused: false));
    }

    [Fact]
    public void DeliveredShortcutWithoutReactionIsReportedAfterAShortWait()
    {
        // The case where a key release went missing and the dictation hook still thinks a key is held.
        var test = new ShortcutTestSession(["Ctrl+\\"], 0);
        test.Delivered(Ctrl, true, 100);
        test.Delivered(Backslash, true, 110);
        Assert.Equal(ShortcutTestVerdict.Waiting, test.Verdict(110 + ShortcutTestSession.ResponseMilliseconds - 1, paused: false));
        Assert.Equal(ShortcutTestVerdict.NotResponding, test.Verdict(110 + ShortcutTestSession.ResponseMilliseconds, paused: false));
    }

    [Fact]
    public void PausedShortcutsAreNamedAsTheCause()
    {
        var test = new ShortcutTestSession(["F2"], 0);
        test.Delivered(F2, true, 100);
        Assert.Equal(ShortcutTestVerdict.Paused, test.Verdict(5000, paused: true));
    }

    [Fact]
    public void FunctionKeyThatArrivesAsMediaKeyPointsToFnLock()
    {
        var test = new ShortcutTestSession(["F2"], 0);
        test.Delivered(BrightnessLikeMediaKey, true, 100);
        test.Delivered(BrightnessLikeMediaKey, false, 150);
        Assert.Equal(ShortcutTestVerdict.Waiting, test.Verdict(ShortcutTestSession.ListenMilliseconds - 1, paused: false));
        Assert.Equal(ShortcutTestVerdict.DifferentKeys, test.Verdict(ShortcutTestSession.ListenMilliseconds, paused: false));
        Assert.Equal("VOLUMEUP", test.Seen);
        Assert.True(test.FunctionKeyArrivedAsMediaKey);
    }

    [Fact]
    public void NothingDeliveredIsReportedAfterTheListeningTime()
    {
        var test = new ShortcutTestSession(["F2"], 0);
        Assert.Equal(ShortcutTestVerdict.NothingReceived, test.Verdict(ShortcutTestSession.ListenMilliseconds, paused: false));
        Assert.True(test.ExpectsFunctionKey);
        Assert.False(test.FunctionKeyArrivedAsMediaKey);
    }

    [Fact]
    public void ExtraHeldKeyShowsUpInTheDeliveredChord()
    {
        var test = new ShortcutTestSession(["Ctrl+\\"], 0);
        test.Delivered(Shift, true, 50);
        test.Delivered(Ctrl, true, 100);
        test.Delivered(Backslash, true, 110);
        Assert.Equal("CTRL+SHIFT+\\", test.Seen);
        Assert.Equal(ShortcutTestVerdict.DifferentKeys, test.Verdict(ShortcutTestSession.ListenMilliseconds, paused: false));
        Assert.False(test.FunctionKeyArrivedAsMediaKey);
    }

    [Fact]
    public void ReactionToAnotherShortcutDoesNotCount()
    {
        var test = new ShortcutTestSession(["F2"], 0);
        test.Respond("F3");
        Assert.Null(test.Responded);
        Assert.Equal(ShortcutTestVerdict.Waiting, test.Verdict(100, paused: false));
    }

    [Fact]
    public void MouseButtonShortcutCanBeTested()
    {
        var test = new ShortcutTestSession(["MouseBack"], 0);
        test.Delivered(0x05, true, 100);
        Assert.Equal("MOUSEBACK", test.Seen);
        test.Respond("MouseBack");
        Assert.Equal(ShortcutTestVerdict.Works, test.Verdict(120, paused: false));
    }

    [Fact]
    public void ShortcutPressedInTheWrongOrderIsNamedAsSuch()
    {
        // The dictation hook ignores Ctrl+\ when \ comes first, so this must not look like a dead hook.
        var test = new ShortcutTestSession(["Ctrl+\\"], 0);
        test.Delivered(Backslash, true, 100);
        test.Delivered(Ctrl, true, 120);
        Assert.Null(test.DeliveredChord);
        Assert.Equal(ShortcutTestVerdict.Waiting, test.Verdict(5000, paused: false));
        Assert.Equal(ShortcutTestVerdict.WrongOrder, test.Verdict(ShortcutTestSession.ListenMilliseconds, paused: false));
    }

    [Fact]
    public void CorrectOrderAfterReleasingEverythingCountsAgain()
    {
        var test = new ShortcutTestSession(["Ctrl+\\"], 0);
        test.Delivered(Backslash, true, 100);
        test.Delivered(Ctrl, true, 120);
        test.Delivered(Ctrl, false, 200);
        test.Delivered(Backslash, false, 210);
        test.Delivered(Ctrl, true, 300);
        test.Delivered(Backslash, true, 310);
        Assert.Equal("CTRL+\\", test.DeliveredChord);
        Assert.Equal(ShortcutTestVerdict.NotResponding, test.Verdict(310 + ShortcutTestSession.ResponseMilliseconds, paused: false));
    }

    [Fact]
    public void ModifierOnlyShortcutIsSpoiledByAnotherKeyInBetween()
    {
        var test = new ShortcutTestSession(["Ctrl+Shift"], 0);
        test.Delivered(Ctrl, true, 100);
        test.Delivered(0x41, true, 110);
        test.Delivered(Shift, true, 120);
        Assert.Null(test.DeliveredChord);
        Assert.Equal(ShortcutTestVerdict.DifferentKeys, test.Verdict(ShortcutTestSession.ListenMilliseconds, paused: false));
    }
}
