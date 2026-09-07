using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationTextPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-text-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "text.json");

    [Fact]
    public void MissingProfileUsesDefaultsWithoutWriting()
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Equal(new DictationTextPreferences(), store.Current);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void AllChoicesSurviveReloadAndEarlierSnapshotRemainsImmutable()
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        var captured = store.Current;
        var next = new DictationTextPreferences
        {
            TranscribeShortQuietClipsAggressively = true,
            TranscriptionNumberNormalizationEnabled = false,
            ShortUtterancePunctuationEnabled = false,
            EnglishOutputVariant = EnglishOutputVariant.UnitedKingdom,
            GermanOutputVariant = GermanOutputVariant.Switzerland,
            AppFormattingEnabled = true,
            SpokenFormattingProfiles = [new()
            {
                EngineId = "sherpa-onnx", ModelId = "parakeet-tdt-0.6b", LanguageCode = "de",
                StrategyOverrideRaw = "automatic"
            }]
        };
        Assert.Null(store.Save(next));
        AssertContentEqual(next, new DictationTextPreferencesStore(PreferencesPath).Current);
        AssertContentEqual(new DictationTextPreferences(), captured);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("broken")]
    [InlineData("{\"TranscriptionNumberNormalizationEnabled\":false,\"ShortUtterancePunctuationEnabled\":false,\"EnglishOutputVariant\":\"Unknown\",\"GermanOutputVariant\":\"AsTranscribed\"}")]
    [InlineData("{\"TranscriptionNumberNormalizationEnabled\":false,\"ShortUtterancePunctuationEnabled\":false,\"EnglishOutputVariant\":\"AsTranscribed\",\"GermanOutputVariant\":\"Austria\"}")]
    public void InvalidPreferencesAreReportedAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Equal(new DictationTextPreferences(), store.Current);
        Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(PreferencesPath));
        Assert.Null(store.Save(new()));
        Assert.Null(new DictationTextPreferencesStore(PreferencesPath).Error);
    }

    [Fact]
    public void FailedSaveRetainsCurrentPreferencesAndCleansTemporaryFile()
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        var saved = new DictationTextPreferences { ShortUtterancePunctuationEnabled = false };
        Assert.Null(store.Save(saved));
        var capturedCurrent = store.Current;
        File.Delete(PreferencesPath);
        Directory.CreateDirectory(PreferencesPath);
        Assert.NotNull(store.Save(saved with { TranscriptionNumberNormalizationEnabled = false, TranscribeShortQuietClipsAggressively = true }));
        Assert.Same(capturedCurrent, store.Current);
        AssertContentEqual(saved, store.Current);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(GermanOutputVariant.Germany)]
    [InlineData(GermanOutputVariant.Austria)]
    [InlineData((GermanOutputVariant)99)]
    public void UnsupportedGermanVariantsCannotBeSaved(GermanOutputVariant variant)
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.NotNull(store.Save(new() { GermanOutputVariant = variant }));
        Assert.False(File.Exists(PreferencesPath));
    }

    private static void AssertContentEqual(DictationTextPreferences expected, DictationTextPreferences actual)
    {
        Assert.Equal(expected.TranscribeShortQuietClipsAggressively, actual.TranscribeShortQuietClipsAggressively);
        Assert.Equal(expected.TranscriptionNumberNormalizationEnabled, actual.TranscriptionNumberNormalizationEnabled);
        Assert.Equal(expected.ShortUtterancePunctuationEnabled, actual.ShortUtterancePunctuationEnabled);
        Assert.Equal(expected.EnglishOutputVariant, actual.EnglishOutputVariant);
        Assert.Equal(expected.GermanOutputVariant, actual.GermanOutputVariant);
        Assert.Equal(expected.AppFormattingEnabled, actual.AppFormattingEnabled);
        Assert.Equal(expected.SpokenFormattingProfiles.ToArray(), actual.SpokenFormattingProfiles.ToArray());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
