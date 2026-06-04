namespace TypeWhisper.PluginSystem.Tests;

public sealed class HotkeyChipLayoutTests
{
    [Fact]
    public void HotkeyRecorderControl_DefaultStyleUsesChipWithClearAffordance()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Styles",
            "UnifiedSettingsStyles.xaml");

        Assert.Contains("x:Name=\"ClearButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"ActionButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"ActionDivider\"", xaml);
        Assert.DoesNotContain("ActionCommand", xaml);
        Assert.Contains("x:Name=\"AddGlyph\"", xaml);
        Assert.Contains("UseAddGlyph", xaml);
        Assert.Contains("Text=\"&#xE710;\"", xaml);
        Assert.Contains("Property=\"MinWidth\" Value=\"34\"", xaml);
        Assert.Contains("Hotkey.Clear", xaml);
        Assert.Contains("CornerRadius=\"16\"", xaml);
    }

    [Fact]
    public void HotkeyRecorderControl_EmptyStateDoesNotOverlapRecordingState()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Styles",
            "UnifiedSettingsStyles.xaml");

        Assert.Contains("<MultiDataTrigger>", xaml);
        Assert.Contains("Property=\"IsRecording\"", xaml);
        Assert.Contains("Property=\"Visibility\" Value=\"Visible\"", xaml);
        Assert.DoesNotContain("<DataTrigger Binding=\"{Binding Hotkey, RelativeSource={RelativeSource Self}}\" Value=\"\">", xaml);
    }

    [Fact]
    public void HotkeyRecorderControl_LeavesTemplateButtonClicksForButtons()
    {
        var code = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Controls",
            "HotkeyRecorderControl.cs");

        Assert.Contains("RecordedCommand", code);
        Assert.Contains("CommitRecordedHotkey(hotkey)", code);
        Assert.DoesNotContain("ActionButton", code);
        Assert.Contains("IsTemplateButtonClick", code);
        Assert.Contains("return;", code);
    }

    [Fact]
    public void WorkflowsSection_UsesHotkeyCollectionChips()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "WorkflowsSection.xaml");

        Assert.Contains("Workflows.EditHotkeys", xaml);
        Assert.Contains("Workflows.RemoveHotkeyCommand", xaml);
        Assert.Contains("Workflows.NewHotkey", xaml);
        Assert.Contains("RecordedCommand=\"{Binding Workflows.AddHotkeyCommand}\"", xaml);
        Assert.Contains("UseAddGlyph=\"True\"", xaml);
        Assert.DoesNotContain("Width=\"292\"", xaml);
        Assert.DoesNotContain("ActionCommand", xaml);
        Assert.DoesNotContain("Workflows.EditHotkey, Mode=TwoWay", xaml);
    }

    [Fact]
    public void WorkflowsSection_ShowsEditorValidationToastOutsideScrollViewer()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "WorkflowsSection.xaml");

        Assert.Contains("<RowDefinition Height=\"Auto\"/>", xaml);
        Assert.Contains("<RowDefinition Height=\"*\"/>", xaml);
        Assert.Contains("<ui:InfoBar Grid.Row=\"1\"", xaml);
        Assert.Contains("<ScrollViewer Grid.Row=\"2\"", xaml);

        var infoBarIndex = xaml.IndexOf("<ui:InfoBar Grid.Row=\"1\"", StringComparison.Ordinal);
        var scrollViewerIndex = xaml.IndexOf("<ScrollViewer Grid.Row=\"2\"", StringComparison.Ordinal);
        Assert.True(infoBarIndex >= 0);
        Assert.True(scrollViewerIndex > infoBarIndex);

        var scrollContent = xaml[scrollViewerIndex..];
        Assert.DoesNotContain("Workflows.ValidationTitle", scrollContent);
    }

    [Fact]
    public void ShortcutsAndWelcomeUseHotkeyCollectionChips()
    {
        var shortcutsXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "ShortcutsSection.xaml");
        var welcomeXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("ItemsSource=\"{Binding Settings.MainDictationHotkeys}\"", shortcutsXaml);
        Assert.Contains("Settings.NewMainDictationHotkey", shortcutsXaml);
        Assert.Contains("RecordedCommand=\"{Binding Settings.AddMainDictationHotkeyCommand}\"", shortcutsXaml);
        Assert.Contains("UseAddGlyph=\"True\"", shortcutsXaml);
        Assert.Contains("Settings.RemoveMainDictationHotkeyCommand", shortcutsXaml);
        Assert.DoesNotContain("ActionCommand", shortcutsXaml);
        Assert.DoesNotContain("Width=\"282\"", shortcutsXaml);
        Assert.DoesNotContain("HotkeyAddButtonStyle", shortcutsXaml);
        Assert.DoesNotContain("Hotkey=\"{Binding Settings.PushToTalkHotkey", shortcutsXaml);

        Assert.Contains("ItemsSource=\"{Binding MainDictationHotkeys}\"", welcomeXaml);
        Assert.Contains("NewMainDictationHotkey", welcomeXaml);
        Assert.Contains("RecordedCommand=\"{Binding AddMainDictationHotkeyCommand}\"", welcomeXaml);
        Assert.Contains("UseAddGlyph=\"True\"", welcomeXaml);
        Assert.DoesNotContain("ActionCommand", welcomeXaml);
        Assert.DoesNotContain("Width=\"282\"", welcomeXaml);
        Assert.DoesNotContain("HotkeyAddButtonStyle", welcomeXaml);
        Assert.DoesNotContain("Hotkey=\"{Binding MainDictationHotkey", welcomeXaml);
    }
}
