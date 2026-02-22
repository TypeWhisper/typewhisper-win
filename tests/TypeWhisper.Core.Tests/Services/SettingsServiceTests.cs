using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
        Assert.False(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var sut = new SettingsService(_filePath);
        var settings = AppSettings.Default with { Language = "de", HasCompletedOnboarding = true };

        sut.Save(settings);

        var sut2 = new SettingsService(_filePath);
        Assert.Equal("de", sut2.Current.Language);
        Assert.True(sut2.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void Save_CreatesBackupFile()
    {
        var sut = new SettingsService(_filePath);
        var first = AppSettings.Default with { Language = "en" };
        sut.Save(first);

        var second = AppSettings.Default with { Language = "fr" };
        sut.Save(second);

        var bakPath = _filePath + ".bak";
        Assert.True(File.Exists(bakPath));

        var bakJson = File.ReadAllText(bakPath);
        Assert.Contains("en", bakJson);
    }

    [Fact]
    public void Save_DoesNotLeaveTemp()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default with { Language = "de" });

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Load_CorruptPrimary_FallsBackToBackup()
    {
        // Write valid backup
        var backup = AppSettings.Default with { Language = "de", HasCompletedOnboarding = true };
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(_filePath + ".bak", json);

        // Write corrupt primary
        File.WriteAllText(_filePath, "{{not valid json!!");

        var sut = new SettingsService(_filePath);

        Assert.Equal("de", sut.Current.Language);
        Assert.True(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void Load_CorruptPrimaryAndBackup_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, "{{corrupt}}");
        File.WriteAllText(_filePath + ".bak", "{{also corrupt}}");

        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
    }

    [Fact]
    public void Load_CorruptPrimary_RestoresPrimaryFromBackup()
    {
        var backup = AppSettings.Default with { Language = "de" };
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(_filePath + ".bak", json);
        File.WriteAllText(_filePath, "{{corrupt}}");

        _ = new SettingsService(_filePath);

        // Primary should now be restored from backup
        var primaryJson = File.ReadAllText(_filePath);
        Assert.Contains("de", primaryJson);
    }

    [Fact]
    public void Save_FiresSettingsChangedEvent()
    {
        var sut = new SettingsService(_filePath);
        AppSettings? received = null;
        sut.SettingsChanged += s => received = s;

        var settings = AppSettings.Default with { Language = "es" };
        sut.Save(settings);

        Assert.NotNull(received);
        Assert.Equal("es", received!.Language);
    }
}
