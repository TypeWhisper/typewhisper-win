using System.IO;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class UserDataMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"tw_user_data_migration_{Guid.NewGuid():N}");

    [Fact]
    public void MigrateLegacyAudioIfNeeded_MovesLegacyAudioContentsToUserDataAudioPath()
    {
        var legacyAudio = Path.Join(_root, "TypeWhisper", "Audio");
        var userAudio = Path.Join(_root, "TypeWhisper-UserData", "Audio");
        Directory.CreateDirectory(Path.Join(legacyAudio, "nested"));
        File.WriteAllText(Path.Join(legacyAudio, "recording.wav"), "wav");
        File.WriteAllText(Path.Join(legacyAudio, "transcript.txt"), "txt");
        File.WriteAllText(Path.Join(legacyAudio, "nested", "clip.m4a"), "m4a");

        UserDataMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.Equal("wav", File.ReadAllText(Path.Join(userAudio, "recording.wav")));
        Assert.Equal("txt", File.ReadAllText(Path.Join(userAudio, "transcript.txt")));
        Assert.Equal("m4a", File.ReadAllText(Path.Join(userAudio, "nested", "clip.m4a")));
        Assert.False(Directory.Exists(legacyAudio));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_PreservesConflictingTargetFilesWithStableSuffix()
    {
        var legacyAudio = Path.Join(_root, "legacy");
        var userAudio = Path.Join(_root, "user");
        Directory.CreateDirectory(legacyAudio);
        Directory.CreateDirectory(userAudio);
        File.WriteAllText(Path.Join(legacyAudio, "recording.wav"), "legacy");
        File.WriteAllText(Path.Join(userAudio, "recording.wav"), "existing");
        File.WriteAllText(Path.Join(userAudio, "recording-1.wav"), "existing-one");

        UserDataMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.Equal("existing", File.ReadAllText(Path.Join(userAudio, "recording.wav")));
        Assert.Equal("existing-one", File.ReadAllText(Path.Join(userAudio, "recording-1.wav")));
        Assert.Equal("legacy", File.ReadAllText(Path.Join(userAudio, "recording-2.wav")));
        Assert.False(File.Exists(Path.Join(legacyAudio, "recording.wav")));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_DoesNothingWhenLegacyAudioPathIsMissing()
    {
        var legacyAudio = Path.Join(_root, "missing");
        var userAudio = Path.Join(_root, "user");

        UserDataMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.False(Directory.Exists(userAudio));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_RemovesEmptyLegacyAudioDirectory()
    {
        var legacyAudio = Path.Join(_root, "empty");
        var userAudio = Path.Join(_root, "user");
        Directory.CreateDirectory(legacyAudio);

        UserDataMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.False(Directory.Exists(legacyAudio));
        Assert.False(Directory.Exists(userAudio));
    }

    [Fact]
    public void MigrateLegacyData_MovesPersistentFilesAndDirectories()
    {
        var legacy = Path.Join(_root, "TypeWhisper");
        var userData = Path.Join(_root, "TypeWhisper-UserData");
        Directory.CreateDirectory(Path.Join(legacy, "Plugins", "com.test.plugin"));
        Directory.CreateDirectory(Path.Join(legacy, "Data"));
        File.WriteAllText(Path.Join(legacy, "settings.json"), "settings");
        File.WriteAllText(Path.Join(legacy, "api-token"), "token");
        File.WriteAllText(Path.Join(legacy, "Plugins", "com.test.plugin", "manifest.json"), "plugin");
        File.WriteAllText(Path.Join(legacy, "Data", "typewhisper.db"), "database");

        UserDataMigrationService.MigrateLegacyData(legacy, userData);

        Assert.Equal("settings", File.ReadAllText(Path.Join(userData, "settings.json")));
        Assert.Equal("token", File.ReadAllText(Path.Join(userData, "api-token")));
        Assert.Equal("plugin", File.ReadAllText(Path.Join(userData, "Plugins", "com.test.plugin", "manifest.json")));
        Assert.Equal("database", File.ReadAllText(Path.Join(userData, "Data", "typewhisper.db")));
    }

    [Fact]
    public void MigrateLegacyData_DoesNotOverwriteCanonicalData()
    {
        var legacy = Path.Join(_root, "TypeWhisper");
        var userData = Path.Join(_root, "TypeWhisper-UserData");
        Directory.CreateDirectory(Path.Join(legacy, "Plugins", "com.legacy.plugin"));
        Directory.CreateDirectory(Path.Join(userData, "Plugins", "com.current.plugin"));
        File.WriteAllText(Path.Join(legacy, "settings.json"), "legacy");
        File.WriteAllText(Path.Join(userData, "settings.json"), "current");
        File.WriteAllText(Path.Join(legacy, "Plugins", "com.legacy.plugin", "manifest.json"), "legacy-plugin");
        File.WriteAllText(Path.Join(userData, "Plugins", "com.current.plugin", "manifest.json"), "current-plugin");

        UserDataMigrationService.MigrateLegacyData(legacy, userData);

        Assert.Equal("current", File.ReadAllText(Path.Join(userData, "settings.json")));
        Assert.Equal("legacy", File.ReadAllText(Path.Join(legacy, "settings.json")));
        Assert.Equal("current-plugin", File.ReadAllText(Path.Join(userData, "Plugins", "com.current.plugin", "manifest.json")));
        Assert.Equal("legacy-plugin", File.ReadAllText(Path.Join(userData, "Plugins", "com.legacy.plugin", "manifest.json")));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"Test cleanup skipped missing directory '{_root}': {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Test cleanup could not delete '{_root}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Test cleanup could not access '{_root}': {ex.Message}");
        }
    }
}
