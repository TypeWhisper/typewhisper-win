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
    [InlineData("Notepad", "notepad", 42u, true)]
    [InlineData("Chrome_WidgetWin_1", "chrome", 42u, true)]
    [InlineData("ApplicationFrameWindow", "ApplicationFrameHost", 42u, true)]
    [InlineData("Windows.UI.Core.CoreWindow", "SomeUwpApp", 42u, true)]
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", 42u, false)]
    [InlineData("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost", 42u, false)]
    [InlineData("Windows.UI.Core.CoreWindow", "ShellExperienceHost", 42u, false)]
    [InlineData("Windows.UI.Core.CoreWindow", "textinputhost", 42u, false)]
    [InlineData("Notepad", "notepad", 7u, false)]
    [InlineData("Notepad", null, 0u, false)]
    [InlineData("Shell_TrayWnd", "explorer", 42u, false)]
    [InlineData("CabinetWClass", "explorer", 42u, true)]
    [InlineData("NotifyIconOverflowWindow", "explorer", 42u, false)]
    [InlineData("TopLevelWindowForOverflowXamlIsland", "explorer", 42u, false)]
    [InlineData("Progman", "explorer", 42u, false)]
    [InlineData("WorkerW", "explorer", 42u, false)]
    [InlineData("#32768", "explorer", 42u, false)]
    [InlineData("", "notepad", 42u, false)]
    [InlineData(null, "notepad", 42u, false)]
    public void PasteTargetsOnlyOtherAppWindows(string? className, string? processName, uint processId, bool eligible)
    {
        Assert.Equal(eligible, PasteTargetFilter.IsEligible(className, processName, processId, ownProcessId: 7));
    }
}
