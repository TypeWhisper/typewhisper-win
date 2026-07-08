using System.IO;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class UserAudioMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tw_audio_migration_{Guid.NewGuid():N}");

    [Fact]
    public void MigrateLegacyAudioIfNeeded_MovesLegacyAudioContentsToUserDataAudioPath()
    {
        var legacyAudio = Path.Combine(_root, "TypeWhisper", "Audio");
        var userAudio = Path.Combine(_root, "TypeWhisper-UserData", "Audio");
        Directory.CreateDirectory(Path.Combine(legacyAudio, "nested"));
        File.WriteAllText(Path.Combine(legacyAudio, "recording.wav"), "wav");
        File.WriteAllText(Path.Combine(legacyAudio, "transcript.txt"), "txt");
        File.WriteAllText(Path.Combine(legacyAudio, "nested", "clip.m4a"), "m4a");

        UserAudioMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.Equal("wav", File.ReadAllText(Path.Combine(userAudio, "recording.wav")));
        Assert.Equal("txt", File.ReadAllText(Path.Combine(userAudio, "transcript.txt")));
        Assert.Equal("m4a", File.ReadAllText(Path.Combine(userAudio, "nested", "clip.m4a")));
        Assert.False(Directory.Exists(legacyAudio));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_PreservesConflictingTargetFilesWithStableSuffix()
    {
        var legacyAudio = Path.Combine(_root, "legacy");
        var userAudio = Path.Combine(_root, "user");
        Directory.CreateDirectory(legacyAudio);
        Directory.CreateDirectory(userAudio);
        File.WriteAllText(Path.Combine(legacyAudio, "recording.wav"), "legacy");
        File.WriteAllText(Path.Combine(userAudio, "recording.wav"), "existing");
        File.WriteAllText(Path.Combine(userAudio, "recording-1.wav"), "existing-one");

        UserAudioMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.Equal("existing", File.ReadAllText(Path.Combine(userAudio, "recording.wav")));
        Assert.Equal("existing-one", File.ReadAllText(Path.Combine(userAudio, "recording-1.wav")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(userAudio, "recording-2.wav")));
        Assert.False(File.Exists(Path.Combine(legacyAudio, "recording.wav")));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_DoesNothingWhenLegacyAudioPathIsMissing()
    {
        var legacyAudio = Path.Combine(_root, "missing");
        var userAudio = Path.Combine(_root, "user");

        UserAudioMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.False(Directory.Exists(userAudio));
    }

    [Fact]
    public void MigrateLegacyAudioIfNeeded_RemovesEmptyLegacyAudioDirectory()
    {
        var legacyAudio = Path.Combine(_root, "empty");
        var userAudio = Path.Combine(_root, "user");
        Directory.CreateDirectory(legacyAudio);

        UserAudioMigrationService.MigrateLegacyAudio(legacyAudio, userAudio);

        Assert.False(Directory.Exists(legacyAudio));
        Assert.False(Directory.Exists(userAudio));
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
