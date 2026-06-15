namespace TypeWhisper.PluginSystem.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void MainWindow_RemovesOuterMarginForEdgeDock()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml");

        Assert.Contains("x:Name=\"OverlayChrome\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"8\"/>", xaml);
        Assert.Contains("Value=\"{x:Static models:IndicatorStyle.EdgeDock}\"", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0\"/>", xaml);
    }

    [Fact]
    public void MainWindow_UsesSharpTextRenderingHints()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml");

        Assert.Contains("UseLayoutRounding=\"True\"", xaml);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", xaml);
        Assert.Contains("TextOptions.TextRenderingMode=\"ClearType\"", xaml);
        Assert.Contains("RenderOptions.ClearTypeHint=\"Enabled\"", xaml);
    }

    [Fact]
    public void MainWindow_RepositionsAfterDisplayPowerAndUnlockEvents()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml.cs");

        Assert.Contains("SystemEvents.DisplaySettingsChanged", code);
        Assert.Contains("SystemEvents.PowerModeChanged", code);
        Assert.Contains("SystemEvents.SessionSwitch", code);
        Assert.Contains("PowerModes.Resume", code);
        Assert.Contains("SessionSwitchReason.SessionUnlock", code);
        Assert.Contains("SchedulePrimaryOverlayRecovery", code);
    }

    [Fact]
    public void MainWindow_ReassertsTopmostAfterPrimaryRecovery()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml.cs");

        Assert.Contains("OverlayPlacementTarget.PrimaryMonitor", code);
        Assert.Contains("ReassertTopmost", code);
        Assert.Contains("Topmost = false", code);
        Assert.Contains("Topmost = true", code);
    }

    [Fact]
    public void MainWindow_RepositionsToCursorMonitorWhenOverlayBecomesVisible()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "MainWindow.xaml.cs");

        Assert.Contains("PropertyChangedEventManager.AddHandler", code);
        Assert.Contains("nameof(RecordingOverlayViewModel.IsOverlayVisible)", code);
        Assert.Contains("OnViewModelPropertyChanged", code);
        Assert.Contains("PositionOverlay(OverlayPlacementTarget.CursorMonitor)", code);
    }

    [Fact]
    public void RecordingOverlay_DoesNotBroadcastVisibilityForEveryLevelChange()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "RecordingOverlayViewModel.cs");

        Assert.DoesNotContain("OnPropertyChanged(string.Empty)", code);
        Assert.DoesNotContain("OnPropertyChanged(\"\")", code);
        Assert.Contains("_lastPublishedValues", code);
        Assert.Contains("PublishIfChanged(nameof(IsOverlayVisible), IsOverlayVisible)", code);
        Assert.Contains("PublishIfChanged(nameof(AudioLevel), AudioLevel)", code);
        Assert.Contains("PublishIfChanged(nameof(PartialText), PartialText)", code);
        Assert.Contains("PublishIfChanged(nameof(ShowBuiltInPartialPreview), ShowBuiltInPartialPreview)", code);
        Assert.Contains("UseDictation && _dictation.ShowInlineFeedback", code);
        Assert.Contains("UseDictation && _dictation.ShowDetachedFeedback", code);
        Assert.DoesNotContain("OnPropertyChanged(nameof(PartialText))", code);
        Assert.DoesNotContain("OnPropertyChanged(nameof(ShowBuiltInPartialPreview))", code);
        Assert.DoesNotContain("? _dictation.ShowInlineFeedback : false", code);
        Assert.DoesNotContain("? _dictation.ShowDetachedFeedback : false", code);
    }
}
