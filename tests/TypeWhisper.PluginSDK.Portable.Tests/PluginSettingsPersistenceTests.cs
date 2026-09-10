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
        Assert.False(new VocabularyHostServices(_root).GetSetting<bool>("Enabled"));
    }
}
