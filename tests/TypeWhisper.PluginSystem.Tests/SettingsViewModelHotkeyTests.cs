using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SettingsViewModelHotkeyTests
{
    public SettingsViewModelHotkeyTests()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
    }

    [Fact]
    public void DefaultRecorderSettings_MatchRecorderParityDefaults()
    {
        var settings = AppSettings.Default;

        Assert.True(settings.RecorderMicEnabled);
        Assert.False(settings.RecorderSystemAudioEnabled);
        Assert.Equal("wav", settings.RecorderOutputFormat);
        Assert.Equal("mixed", settings.RecorderTrackMode);
        Assert.Equal("aggressive", settings.RecorderMicDuckingMode);
        Assert.True(settings.RecorderTranscriptionEnabled);
        Assert.Equal("", settings.RecorderToggleHotkey);
        Assert.Empty(settings.RecorderToggleHotkeys);
    }

    [Fact]
    public void LoadsMultipleAppHotkeysAsChipCollections()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            MainDictationHotkeys = ["Ctrl+Alt+D", "Ctrl+Shift+D"],
            RecorderToggleHotkeys = ["Ctrl+Alt+R"],
            WorkflowPaletteHotkeys = ["Ctrl+Alt+W", "Ctrl+Shift+W"]
        });

        var sut = CreateSettingsViewModel(settings);

        Assert.Equal(["Ctrl+Alt+D", "Ctrl+Shift+D"], sut.MainDictationHotkeys);
        Assert.Equal(["Ctrl+Alt+R"], sut.RecorderToggleHotkeys);
        Assert.Equal(["Ctrl+Alt+W", "Ctrl+Shift+W"], sut.WorkflowPaletteHotkeys);
    }

    [Fact]
    public void AddAndRemoveMainDictationHotkeys_PersistsListAndLegacyFirstValue()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            MainDictationHotkeys = ["Ctrl+Alt+D"]
        });
        var sut = CreateSettingsViewModel(settings);

        sut.NewMainDictationHotkey = "Ctrl+Shift+D";
        sut.AddMainDictationHotkeyCommand.Execute(null);
        sut.RemoveMainDictationHotkeyCommand.Execute("Ctrl+Alt+D");

        Assert.Equal(["Ctrl+Shift+D"], sut.MainDictationHotkeys);
        Assert.Equal(["Ctrl+Shift+D"], settings.Current.MainDictationHotkeys);
        Assert.Equal("Ctrl+Shift+D", settings.Current.PushToTalkHotkey);
        Assert.Equal("Ctrl+Shift+D", settings.Current.ToggleHotkey);
        Assert.Equal("", sut.NewMainDictationHotkey);
    }

    [Fact]
    public void AddShortcutHotkeyCommand_AcceptsRecordedHotkeyParameter()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            MainDictationHotkeys = [],
            PushToTalkHotkey = "",
            ToggleHotkey = ""
        });
        var sut = CreateSettingsViewModel(settings);

        sut.AddMainDictationHotkeyCommand.Execute("Ctrl+Shift+D");

        Assert.Equal(["Ctrl+Shift+D"], sut.MainDictationHotkeys);
        Assert.Equal(["Ctrl+Shift+D"], settings.Current.MainDictationHotkeys);
        Assert.Equal("", sut.NewMainDictationHotkey);
    }

    [Fact]
    public void AddShortcutHotkey_RejectsDuplicateAcrossGlobalShortcuts()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            WorkflowPaletteHotkeys = ["Ctrl+Alt+W"]
        });
        var sut = CreateSettingsViewModel(settings);

        sut.NewToggleOnlyHotkey = "Ctrl+Alt+W";
        sut.AddToggleOnlyHotkeyCommand.Execute(null);

        Assert.Empty(sut.ToggleOnlyHotkeys);
        Assert.Equal(["Ctrl+Alt+W"], settings.Current.WorkflowPaletteHotkeys);
        Assert.Contains("Ctrl+Alt+W", sut.ShortcutsError);
        Assert.Equal("", sut.NewToggleOnlyHotkey);
    }

    [Fact]
    public void AddAndRemoveRecorderToggleHotkeys_PersistsListAndLegacyFirstValue()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            RecorderToggleHotkeys = ["Ctrl+Alt+R"]
        });
        var sut = CreateSettingsViewModel(settings);

        sut.NewRecorderToggleHotkey = "Ctrl+Shift+R";
        sut.AddRecorderToggleHotkeyCommand.Execute(null);
        sut.RemoveRecorderToggleHotkeyCommand.Execute("Ctrl+Alt+R");

        Assert.Equal(["Ctrl+Shift+R"], sut.RecorderToggleHotkeys);
        Assert.Equal(["Ctrl+Shift+R"], settings.Current.RecorderToggleHotkeys);
        Assert.Equal("Ctrl+Shift+R", settings.Current.RecorderToggleHotkey);
        Assert.Equal("", sut.NewRecorderToggleHotkey);
    }

    [Fact]
    public void PreviewLevelChange_DoesNotPersistSettings()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = CreateSettingsViewModel(settings);

        sut.PreviewLevel = 0.42f;

        Assert.Equal(0, settings.SaveCount);
    }

    private static SettingsViewModel CreateSettingsViewModel(FakeSettingsService settings)
    {
        var pluginManager = TestPluginManagerFactory.Create(settings);
        var system = new FakeTtsProvider("windows-sapi", "System Voice");
        var speech = new SpeechFeedbackService(settings, pluginManager, system);
        var audio = new AudioRecordingService();
        var api = new ApiServerController(Mock.Of<ILocalApiServer>(), settings);
        var cli = new CliInstallService();

        return new SettingsViewModel(settings, audio, api, cli, speech, dispatchToUi: action => action());
    }
}
