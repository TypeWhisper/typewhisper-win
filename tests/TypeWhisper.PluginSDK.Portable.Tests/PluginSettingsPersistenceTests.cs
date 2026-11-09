using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using Xunit;

public sealed class PluginSettingsPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-plugin-settings-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void EnablementSurvivesNewHostAndPreservesOtherSettings()
    {
        var first = new VocabularyHostServices(_root);
        first.SetSetting("ModelDirectory", "fixture-models");
        first.SetSetting("Enabled", true);
        var second = new VocabularyHostServices(_root);
        Assert.True(second.GetSetting<bool>("Enabled"));
        second.SetSetting("Enabled", false);
        var third = new VocabularyHostServices(_root);
        Assert.False(third.GetSetting<bool>("Enabled"));
        Assert.Equal("fixture-models", third.GetSetting<string>("ModelDirectory"));
    }

    [Fact]
    public void MalformedSettingsCannotBeSilentlyReplaced()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "settings.json");
        File.WriteAllText(file, "{broken");
        var host = new VocabularyHostServices(_root);
        Assert.Throws<System.Text.Json.JsonException>(() => host.SetSetting("Enabled", true));
        Assert.Equal("{broken", File.ReadAllText(file));
    }

    [Fact]
    public void AssetOverrideDoesNotCreateOrMigrateUserData()
    {
        var assets = Path.Combine(_root, "models");
        var host = new VocabularyHostServices(Path.Combine(_root, "settings"), assetDirectory: assets);
        Assert.Equal(assets, host.PluginAssetDirectory);
        Assert.False(((IPluginHostServices)host).AllowLegacyDataMigration);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void FailedSaveIsReportedWithoutInventingEnabledState()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "settings.json.tmp"));
        var host = new VocabularyHostServices(_root);
        var error = Record.Exception(() => host.SetSetting("Enabled", true));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.False(host.GetSetting<bool>("Enabled"));
        Assert.False(new VocabularyHostServices(_root).GetSetting<bool>("Enabled"));
    }

    // Any open handle on the file, even one that shares everything like a virus scanner's, makes Windows refuse to
    // replace it. A scanner checking the last write holds the file for a few milliseconds.
    [Fact]
    public async Task SaveWaitsForAScannerThatBrieflyHoldsTheSettingsFile()
    {
        var host = new VocabularyHostServices(_root);
        host.SetSetting("Model", "a");
        var file = Path.Combine(_root, "settings.json");
        var scanner = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var scanned = Task.Run(async () => { await Task.Delay(100); await scanner.DisposeAsync(); });

        host.SetSetting("Model", "b");

        await scanned;
        Assert.Contains("\"Model\":\"b\"", File.ReadAllText(file));
    }

    [Fact]
    public void SaveStillFailsWhileTheSettingsFileStaysHeld()
    {
        var host = new VocabularyHostServices(_root);
        host.SetSetting("Model", "a");
        var file = Path.Combine(_root, "settings.json");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Throws<UnauthorizedAccessException>(() => host.SetSetting("Model", "b"));

        Assert.Equal("a", host.GetSetting<string>("Model"));
        Assert.Contains("\"Model\":\"a\"", File.ReadAllText(file));
    }

    [Fact]
    public void RepeatedReadsUseTheParsedFileUntilItsTimeOrSizeChanges()
    {
        var host = new VocabularyHostServices(_root);
        host.SetSetting("Mode", "aaaa");
        var file = Path.Combine(_root, "settings.json");
        var written = File.GetLastWriteTimeUtc(file);
        Assert.Equal("aaaa", host.GetSetting<string>("Mode"));
        // Same size and write time: the earlier parse is used, so the file is not read again.
        File.WriteAllText(file, File.ReadAllText(file).Replace("aaaa", "bbbb"));
        File.SetLastWriteTimeUtc(file, written);
        Assert.Equal("aaaa", host.GetSetting<string>("Mode"));
        // Another writer changes the write time, so the file is read again.
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));
        Assert.Equal("bbbb", host.GetSetting<string>("Mode"));
        File.WriteAllText(file, "{\"Mode\":\"a\"}");
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));
        Assert.Equal("a", host.GetSetting<string>("Mode"));
    }

    [Fact]
    public void UnchangedValueIsNotWrittenAgain()
    {
        var host = new VocabularyHostServices(_root);
        host.SetSetting("Enabled", true);
        host.SetSetting<string?>("Model", null);
        var file = Path.Combine(_root, "settings.json");
        var saved = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, saved);
        host.SetSetting("Enabled", true);
        host.SetSetting<string?>("Model", null);
        new VocabularyHostServices(_root).SetSetting("Enabled", true);
        Assert.Equal(saved, File.GetLastWriteTimeUtc(file));
        host.SetSetting("Enabled", false);
        Assert.NotEqual(saved, File.GetLastWriteTimeUtc(file));
        Assert.False(new VocabularyHostServices(_root).GetSetting<bool>("Enabled"));
        Assert.Null(new VocabularyHostServices(_root).GetSetting<string>("Model"));
    }

    [Fact]
    public void WriteThroughOneHostIsTheNextReadOfAnotherHostOfTheFolder()
    {
        var first = new VocabularyHostServices(_root);
        var second = new VocabularyHostServices(_root);
        first.SetSetting("Model", "a");
        Assert.Equal("a", second.GetSetting<string>("Model"));
        var file = Path.Combine(_root, "settings.json");
        var written = File.GetLastWriteTimeUtc(file);
        first.SetSetting("Model", "b");
        // Same size and, as within one file time tick, the same write time: the new value is still read.
        File.SetLastWriteTimeUtc(file, written);
        Assert.Equal("b", second.GetSetting<string>("Model"));
        second.SetSetting("Other", 1);
        Assert.Equal("b", first.GetSetting<string>("Model"));
        Assert.Equal(1, first.GetSetting<int>("Other"));
        Assert.Contains("\"Model\":\"b\"", File.ReadAllText(file));
    }
}
