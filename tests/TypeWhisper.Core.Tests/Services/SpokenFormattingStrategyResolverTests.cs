using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public class SpokenFormattingStrategyResolverTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SettingsService _settings;
    private readonly SpokenFormattingProfileStore _profileStore;
    private readonly SpokenFormattingStrategyResolver _sut;

    public SpokenFormattingStrategyResolverTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), $"tw_spoken_formatting_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _settings = new SettingsService(Path.Join(_tempDirectory, "settings.json"));
        _profileStore = new SpokenFormattingProfileStore(_settings);
        _sut = new SpokenFormattingStrategyResolver(_profileStore, new SpokenFormattingRulesLoader());
    }

    [Fact]
    public void Resolve_MissingProfile_DefaultsToNative()
    {
        var result = _sut.Resolve("sherpa-onnx", "parakeet-tdt-0.6b", ["de"], null);

        Assert.NotNull(result);
        Assert.Equal("de", result.LanguageCode);
        Assert.Equal(SpokenFormattingStrategy.NativeOnly, result.Strategy);
        Assert.Equal(SpokenFormattingVerificationState.VendorHint, result.Profile.VerificationState);
    }

    [Fact]
    public void Resolve_SingleConfiguredLanguageWinsOverDetectedLanguage()
    {
        var result = _sut.Resolve("engine", "model", ["de-DE"], "en-US");

        Assert.Equal("de", result!.LanguageCode);
    }

    [Fact]
    public void Resolve_MultipleConfiguredLanguagesPreferDetectedLanguage()
    {
        var result = _sut.Resolve("engine", "model", ["de", "en"], "en-US");

        Assert.Equal("en", result!.LanguageCode);
    }

    [Fact]
    public void Resolve_MultipleConfiguredLanguagesFallBackToFirstSupportedHint()
    {
        var result = _sut.Resolve("engine", "model", ["de", "en"], null);

        Assert.Equal("de", result!.LanguageCode);
    }

    [Fact]
    public void Resolve_FreeAutoDetectionUsesDetectedSupportedLanguage()
    {
        var result = _sut.Resolve("engine", "model", [], "en-US");

        Assert.Equal("en", result!.LanguageCode);
    }

    [Fact]
    public void Resolve_UnsupportedContext_ReturnsNull()
    {
        Assert.Null(_sut.Resolve(null, "model", ["de"], null));
        Assert.Null(_sut.Resolve("engine", "model", ["fr"], "fr-FR"));
        Assert.Null(_sut.Resolve("engine", "model", ["fr"], "de-DE"));
    }

    [Fact]
    public void SaveUserOverride_RoundTripsByEngineModelAndLanguage()
    {
        _profileStore.SaveUserOverride(
            " engine ",
            " model ",
            "DE-de",
            SpokenFormattingStrategy.FallbackOnly,
            SpokenFormattingVerificationState.UserVerifiedBad,
            updateVerificationDate: true);

        var reloadedSettings = new SettingsService(Path.Join(_tempDirectory, "settings.json"));
        var reloadedResolver = new SpokenFormattingStrategyResolver(
            new SpokenFormattingProfileStore(reloadedSettings),
            new SpokenFormattingRulesLoader());
        var result = reloadedResolver.Resolve("engine", "model", ["de"], null);

        Assert.Equal(SpokenFormattingStrategy.FallbackOnly, result!.Strategy);
        Assert.Equal(SpokenFormattingVerificationState.UserVerifiedBad, result.Profile.VerificationState);
        Assert.NotNull(result.Profile.LastVerifiedAt);
    }

    [Fact]
    public void SettingsNormalization_PreservesUnknownFutureProfileValues()
    {
        _settings.Save(_settings.Current with
        {
            SpokenFormattingProfiles =
            [
                new DictationSpokenFormattingProfile
                {
                    EngineId = "engine",
                    ModelId = "model",
                    LanguageCode = "en-US",
                    StrategyOverrideRaw = "futureStrategy",
                    VerificationStateRaw = "futureVerification"
                }
            ]
        });

        var profile = Assert.Single(new SettingsService(Path.Join(_tempDirectory, "settings.json"))
            .Current.SpokenFormattingProfiles);

        Assert.Equal("futureStrategy", profile.StrategyOverrideRaw);
        Assert.Equal("futureVerification", profile.VerificationStateRaw);
        Assert.Null(profile.StrategyOverride);
        Assert.Equal(SpokenFormattingVerificationState.Unknown, profile.VerificationState);

        var resolver = new SpokenFormattingStrategyResolver(
            new SpokenFormattingProfileStore(new SettingsService(Path.Join(_tempDirectory, "settings.json"))),
            new SpokenFormattingRulesLoader());
        var resolved = resolver.Resolve("engine", "model", ["en"], null);

        Assert.Equal(SpokenFormattingStrategy.NativeOnly, resolved!.Strategy);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
