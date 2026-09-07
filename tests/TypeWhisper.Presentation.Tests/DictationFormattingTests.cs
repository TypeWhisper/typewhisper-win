using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationFormattingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-formatting-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "text.json");
    private static DictationTextPreferences Profile(SpokenFormattingStrategy strategy, string language = "en") =>
        DictationFormatting.WithProfile(new(), "sherpa-onnx", "parakeet-tdt-0.6b", language, strategy);

    [Theory]
    [InlineData(true, "Obsidian", "- Hello")]
    [InlineData(false, "Obsidian", "bullet Hello")]
    [InlineData(true, "OUTLOOK", "bullet Hello")]
    [InlineData(true, "Code", "bullet Hello")]
    [InlineData(true, null, "bullet Hello")]
    public async Task AppFormattingUsesRealTargetAndOnlyChangesSupportedMarkdownApps(bool enabled, string? process, string expected)
    {
        var result = await DictationTextPipeline.ProcessAsync("bullet Hello", new() { AppFormattingEnabled = enabled }, "en", targetProcessName: process);
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData(SpokenFormattingStrategy.NativeOnly, "Hello comma world", "Hello comma world")]
    [InlineData(SpokenFormattingStrategy.Automatic, "Hello comma world", "Hello, world")]
    [InlineData(SpokenFormattingStrategy.Automatic, "Hello  world", "Hello  world")]
    [InlineData(SpokenFormattingStrategy.FallbackOnly, "Hello  world", "Hello world")]
    public async Task SavedStrategyControlsActualCoreFormatting(SpokenFormattingStrategy strategy, string input, string expected)
    {
        var result = await DictationTextPipeline.ProcessAsync(input, Profile(strategy), "en",
            engineId: "sherpa-onnx", modelId: "parakeet-tdt-0.6b");
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("sherpa-onnx", "other-model", "en", null)]
    [InlineData("groq", "parakeet-tdt-0.6b", "en", null)]
    [InlineData("sherpa-onnx", "parakeet-tdt-0.6b", "fr", "fr")]
    [InlineData("sherpa-onnx", "parakeet-tdt-0.6b", "auto", null)]
    public async Task OtherModelsLanguagesAndUnrecognizedAutoDoNotUseUnrelatedProfile(string engine, string model, string configured, string? detected)
    {
        var result = await DictationTextPipeline.ProcessAsync("Hello comma world", Profile(SpokenFormattingStrategy.Automatic), configured,
            detectedLanguage: detected, engineId: engine, modelId: model);
        Assert.Equal("Hello comma world", result.Text);
    }

    [Fact]
    public async Task AutoUsesDetectedGermanAndTranslationUsesEnglishProfile()
    {
        var preferences = DictationFormatting.WithProfile(Profile(SpokenFormattingStrategy.Automatic, "de"),
            "sherpa-onnx", "parakeet-tdt-0.6b", "en", SpokenFormattingStrategy.FallbackOnly);
        var german = await DictationTextPipeline.ProcessAsync("Hallo neue Zeile Welt", preferences, "auto",
            detectedLanguage: "de-DE", engineId: "sherpa-onnx", modelId: "parakeet-tdt-0.6b");
        Assert.Equal("Hallo\nWelt", german.Text);
        var translated = await DictationTextPipeline.ProcessAsync("Hello new line world", preferences, "de",
            detectedLanguage: "de", task: TranscriptionTask.Translate, engineId: "sherpa-onnx", modelId: "parakeet-tdt-0.6b");
        Assert.Equal("Hello\nworld", translated.Text);
    }

    [Fact]
    public async Task AppAndSpokenFormattingRunBeforeSnippetExpansion()
    {
        string? snippetInput = null;
        var result = await DictationTextPipeline.ProcessAsync("bullet Hello comma world",
            Profile(SpokenFormattingStrategy.Automatic) with { AppFormattingEnabled = true }, "en",
            expandSnippets: (text, _) => { snippetInput = text; return Task.FromResult("replacement new line item"); },
            targetProcessName: "Obsidian", engineId: "sherpa-onnx", modelId: "parakeet-tdt-0.6b");
        Assert.Equal("- Hello, world", snippetInput);
        Assert.Equal("replacement new line item", result.Text);
    }

    [Fact]
    public void ExistingFourFieldPreferencesLoadWithoutResettingChoices()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, """
            {"TranscriptionNumberNormalizationEnabled":false,"ShortUtterancePunctuationEnabled":false,
             "EnglishOutputVariant":"UnitedKingdom","GermanOutputVariant":"Switzerland"}
            """);
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Null(store.Error);
        Assert.False(store.Current.TranscriptionNumberNormalizationEnabled);
        Assert.False(store.Current.AppFormattingEnabled);
        Assert.Empty(store.Current.SpokenFormattingProfiles);
        Assert.Equal(EnglishOutputVariant.UnitedKingdom, store.Current.EnglishOutputVariant);
    }

    [Fact]
    public void ProfilesAndAppPreferencePersistWithoutInventingVerification()
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(Profile(SpokenFormattingStrategy.Automatic) with { AppFormattingEnabled = true }));
        var restarted = new DictationTextPreferencesStore(PreferencesPath);
        Assert.True(restarted.Current.AppFormattingEnabled);
        var profile = Assert.Single(restarted.Current.SpokenFormattingProfiles);
        Assert.Equal("sherpa-onnx", profile.EngineId);
        Assert.Equal("parakeet-tdt-0.6b", profile.ModelId);
        Assert.Equal("automatic", profile.StrategyOverrideRaw);
        Assert.Equal(SpokenFormattingVerificationState.Unknown, profile.VerificationState);
        Assert.Null(profile.LastVerifiedAt);
    }

    [Fact]
    public void SavedSnapshotCannotBeChangedByMutatingOriginalProfileList()
    {
        var profiles = Profile(SpokenFormattingStrategy.Automatic).SpokenFormattingProfiles.ToList();
        var store = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(new() { SpokenFormattingProfiles = profiles }));
        var captured = store.Current;
        profiles.Clear();
        Assert.Single(captured.SpokenFormattingProfiles);
        Assert.Null(store.Save(DictationFormatting.WithProfile(store.Current, "sherpa-onnx", "parakeet-tdt-0.6b", "en", SpokenFormattingStrategy.NativeOnly)));
        Assert.Equal("automatic", Assert.Single(captured.SpokenFormattingProfiles).StrategyOverrideRaw);
    }

    [Fact]
    public void DuplicateProfileKeysAreRejectedAndUnknownStrategyStringsSurvive()
    {
        var store = new DictationTextPreferencesStore(PreferencesPath);
        var profile = Assert.Single(Profile(SpokenFormattingStrategy.Automatic).SpokenFormattingProfiles);
        Assert.NotNull(store.Save(new() { SpokenFormattingProfiles = [profile, profile with { EngineId = "SHERPA-ONNX" }] }));
        var future = profile with { StrategyOverrideRaw = "futureStrategy", VerificationStateRaw = "futureVerification" };
        Assert.Null(store.Save(new() { SpokenFormattingProfiles = [future] }));
        var restarted = new DictationTextPreferencesStore(PreferencesPath);
        Assert.Equal("futureStrategy", Assert.Single(restarted.Current.SpokenFormattingProfiles).StrategyOverrideRaw);
        Assert.Equal(SpokenFormattingStrategy.NativeOnly, DictationFormatting.Resolve(restarted.Current, "sherpa-onnx", "parakeet-tdt-0.6b", "en", null)!.Strategy);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
