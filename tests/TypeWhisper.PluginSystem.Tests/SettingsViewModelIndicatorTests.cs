using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class SettingsViewModelIndicatorTests
{
    [Fact]
    public void LoadsIndicatorStyleAndLivePreviewFromSettings()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            IndicatorStyle = IndicatorStyle.EdgeDock,
            LiveTranscriptionEnabled = false,
            OnlineAsrBatchLiveTranscriptionEnabled = true,
            LiveTranscriptionFontSize = 15
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.Equal(IndicatorStyle.EdgeDock, sut.IndicatorStyle);
        Assert.False(sut.LiveTranscriptionEnabled);
        Assert.True(sut.OnlineAsrBatchLiveTranscriptionEnabled);
        Assert.Equal(15, sut.LiveTranscriptionFontSize);
    }

    [Fact]
    public void ChangingIndicatorStyleAndLivePreviewPersistsSettings()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = CreateSettingsViewModel(settings);

        sut.IndicatorStyle = IndicatorStyle.CompactBadge;
        sut.LiveTranscriptionEnabled = false;
        sut.OnlineAsrBatchLiveTranscriptionEnabled = true;
        sut.LiveTranscriptionFontSize = 16.5;

        Assert.Equal(IndicatorStyle.CompactBadge, settings.Current.IndicatorStyle);
        Assert.False(settings.Current.LiveTranscriptionEnabled);
        Assert.True(settings.Current.OnlineAsrBatchLiveTranscriptionEnabled);
        Assert.Equal(16.5, settings.Current.LiveTranscriptionFontSize);
    }

    [Fact]
    public void EnglishOutputVariant_LoadsOptionsAndPersistsSelection()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            EnglishOutputVariant = EnglishOutputVariant.UnitedKingdom
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.Equal(EnglishOutputVariant.UnitedKingdom, sut.EnglishOutputVariant);
        Assert.Equal(3, sut.EnglishOutputVariantOptions.Count);

        sut.EnglishOutputVariant = EnglishOutputVariant.UnitedStates;

        Assert.Equal(EnglishOutputVariant.UnitedStates, settings.Current.EnglishOutputVariant);
    }

    [Fact]
    public void GermanOutputVariant_LoadsOptionsAndPersistsSelection()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            GermanOutputVariant = GermanOutputVariant.Austria
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.Equal(GermanOutputVariant.Austria, sut.GermanOutputVariant);
        Assert.Equal(4, sut.GermanOutputVariantOptions.Count);

        sut.GermanOutputVariant = GermanOutputVariant.Switzerland;

        Assert.Equal(GermanOutputVariant.Switzerland, settings.Current.GermanOutputVariant);
    }

    [Fact]
    public void ShortUtterancePunctuation_LoadsAndPersistsSetting()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            ShortUtterancePunctuationEnabled = false
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.False(sut.ShortUtterancePunctuationEnabled);

        sut.ShortUtterancePunctuationEnabled = true;

        Assert.True(settings.Current.ShortUtterancePunctuationEnabled);
    }

    [Fact]
    public void EnglishOutputVariantVisibility_IncludesEnglishTranslationTarget()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LanguageHints = ["de"],
            TranslationTargetLanguage = "en-US"
        });
        var sut = CreateSettingsViewModel(settings);
        var changedProperties = new List<string?>();
        sut.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        var saveCountBeforeChange = settings.SaveCount;

        Assert.True(sut.HasSelectedEnglishLanguage);

        sut.TranslationTargetLanguage = "fr";

        Assert.False(sut.HasSelectedEnglishLanguage);
        Assert.Contains(nameof(SettingsViewModel.HasSelectedEnglishLanguage), changedProperties);
        Assert.Equal(saveCountBeforeChange + 1, settings.SaveCount);
    }

    [Fact]
    public void EnglishOutputVariantVisibility_IncludesUnrestrictedLanguageDetection()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LanguageHints = []
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.True(sut.HasNoSelectedLanguageHints);
        Assert.True(sut.HasSelectedEnglishLanguage);
    }

    [Fact]
    public void GermanOutputVariantVisibility_IncludesGermanTranslationTarget()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LanguageHints = ["en"],
            TranslationTargetLanguage = "de-CH"
        });
        var sut = CreateSettingsViewModel(settings);
        var changedProperties = new List<string?>();
        sut.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        var saveCountBeforeChange = settings.SaveCount;

        Assert.True(sut.HasSelectedGermanLanguage);

        sut.TranslationTargetLanguage = "fr";

        Assert.False(sut.HasSelectedGermanLanguage);
        Assert.Contains(nameof(SettingsViewModel.HasSelectedGermanLanguage), changedProperties);
        Assert.Equal(saveCountBeforeChange + 1, settings.SaveCount);
    }

    [Fact]
    public void LoadsAndPersistsApiAuthenticationSetting()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            ApiServerRequiresAuthentication = true
        });
        var sut = CreateSettingsViewModel(settings);

        Assert.True(sut.ApiServerRequiresAuthentication);

        sut.ApiServerRequiresAuthentication = false;

        Assert.False(settings.Current.ApiServerRequiresAuthentication);
    }

    [Fact]
    public void TranslationModeEnabled_LoadsFromTranslationTarget()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = "fr",
            LastTranslationTargetLanguage = "fr"
        });

        var sut = CreateSettingsViewModel(settings);

        Assert.True(sut.QuickTranslationModeEnabled);
    }

    [Fact]
    public void QuickTranslationModeEnabled_RestoresLastTarget()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = null,
            LastTranslationTargetLanguage = "fr"
        });
        var sut = CreateSettingsViewModel(settings);

        sut.QuickTranslationModeEnabled = true;

        Assert.Equal("fr", settings.Current.TranslationTargetLanguage);
        Assert.Equal("fr", settings.Current.LastTranslationTargetLanguage);
    }

    [Fact]
    public void QuickTranslationModeDisabled_ClearsTargetButKeepsLastTarget()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            TranslationTargetLanguage = "de",
            LastTranslationTargetLanguage = "de"
        });
        var sut = CreateSettingsViewModel(settings);

        sut.QuickTranslationModeEnabled = false;

        Assert.Null(settings.Current.TranslationTargetLanguage);
        Assert.Equal("de", settings.Current.LastTranslationTargetLanguage);
    }

    [Fact]
    public void TranslationTargetSelection_StoresLastTarget_WhenRealLanguageSelected()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = CreateSettingsViewModel(settings);

        sut.TranslationTargetLanguage = "it";

        Assert.Equal("it", settings.Current.TranslationTargetLanguage);
        Assert.Equal("it", settings.Current.LastTranslationTargetLanguage);
        Assert.True(sut.QuickTranslationModeEnabled);
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
