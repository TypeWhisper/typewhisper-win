using TypeWhisper.Presentation;
using Xunit;

public sealed class LastDictationPasteTests
{
    private static readonly LastCompletedDictation Snapshot = new("id", "Final text", "en", "engine", "model", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task PastesExactFinalTextOnce()
    {
        var pasted = new List<string>();
        var result = await LastDictationPaste.RunAsync(Snapshot, false, false, true, text => { pasted.Add(text); return Task.FromResult(true); });
        Assert.Equal(LastDictationPasteResult.Pasted, result);
        Assert.Equal(["Final text"], pasted);
    }

    [Theory]
    [InlineData(true, false, true, LastDictationPasteResult.Ignored)]
    [InlineData(false, true, true, LastDictationPasteResult.Busy)]
    [InlineData(true, true, true, LastDictationPasteResult.Ignored)]
    [InlineData(false, false, false, LastDictationPasteResult.NoTarget)]
    public async Task RefusesWithoutTouchingThePasteBoundary(bool blocked, bool busy, bool hasTarget, LastDictationPasteResult expected)
    {
        var calls = 0;
        var result = await LastDictationPaste.RunAsync(Snapshot, blocked, busy, hasTarget, _ => { calls++; return Task.FromResult(true); });
        Assert.Equal(expected, result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task EmptySessionIsReportedBeforeTheTarget(string? text)
    {
        var snapshot = text is null ? null : Snapshot with { Text = text };
        Assert.Equal(LastDictationPasteResult.Empty,
            await LastDictationPaste.RunAsync(snapshot, false, false, false, _ => Task.FromResult(true)));
    }

    [Fact]
    public async Task RejectedOrFailedPasteIsNotReportedAsPasted()
    {
        Assert.Equal(LastDictationPasteResult.NotPasted,
            await LastDictationPaste.RunAsync(Snapshot, false, false, true, _ => Task.FromResult(false)));
        Assert.Equal(LastDictationPasteResult.NotPasted,
            await LastDictationPaste.RunAsync(Snapshot, false, false, true, _ => throw new InvalidOperationException("clipboard")));
    }

    [Theory]
    [InlineData("Notepad", 42u, true)]
    [InlineData("Chrome_WidgetWin_1", 42u, true)]
    [InlineData("Notepad", 7u, false)]
    [InlineData("Notepad", 0u, false)]
    [InlineData("Shell_TrayWnd", 42u, false)]
    [InlineData("NotifyIconOverflowWindow", 42u, false)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", 42u, false)]
    [InlineData("Progman", 42u, false)]
    [InlineData("WorkerW", 42u, false)]
    [InlineData("#32768", 42u, false)]
    [InlineData("", 42u, false)]
    [InlineData(null, 42u, false)]
    public void TrayPasteTargetsOnlyOtherAppWindows(string? className, uint processId, bool eligible)
    {
        Assert.Equal(eligible, PasteTargetFilter.IsEligible(className, processId, ownProcessId: 7));
    }
}
