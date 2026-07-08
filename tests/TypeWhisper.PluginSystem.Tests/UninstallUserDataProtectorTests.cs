using System.IO;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class UninstallUserDataProtectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tw_uninstall_protector_{Guid.NewGuid():N}");

    [Fact]
    public void ProtectLegacyAudioDirectory_SendsLegacyAudioDirectoryToRecycleBin()
    {
        var legacyAudio = Path.Combine(_root, "TypeWhisper", "Audio");
        var recoveryRoot = Path.Combine(_root, "TypeWhisper-Recovered");
        Directory.CreateDirectory(legacyAudio);
        File.WriteAllText(Path.Combine(legacyAudio, "recording.wav"), "wav");
        var recycle = new FakeRecycleBinOperation(path =>
        {
            Directory.Delete(path, recursive: true);
            return true;
        });
        var protector = new UninstallUserDataProtector(
            recycle,
            () => new DateTimeOffset(2026, 7, 8, 12, 30, 45, TimeSpan.Zero));

        protector.ProtectLegacyAudioDirectory(legacyAudio, recoveryRoot);

        Assert.Equal([legacyAudio], recycle.Paths);
        Assert.False(Directory.Exists(legacyAudio));
        Assert.False(Directory.Exists(recoveryRoot));
    }

    [Fact]
    public void ProtectLegacyAudioDirectory_MovesLegacyAudioDirectoryToRecoveryWhenRecycleBinFails()
    {
        var legacyAudio = Path.Combine(_root, "TypeWhisper", "Audio");
        var recoveryRoot = Path.Combine(_root, "TypeWhisper-Recovered");
        Directory.CreateDirectory(legacyAudio);
        File.WriteAllText(Path.Combine(legacyAudio, "recording.wav"), "wav");
        var recycle = new FakeRecycleBinOperation(_ => false);
        var protector = new UninstallUserDataProtector(
            recycle,
            () => new DateTimeOffset(2026, 7, 8, 12, 30, 45, TimeSpan.Zero));

        protector.ProtectLegacyAudioDirectory(legacyAudio, recoveryRoot);

        var recoveredAudio = Path.Combine(recoveryRoot, "Audio-20260708-123045");
        Assert.Equal([legacyAudio], recycle.Paths);
        Assert.False(Directory.Exists(legacyAudio));
        Assert.Equal("wav", File.ReadAllText(Path.Combine(recoveredAudio, "recording.wav")));
    }

    [Fact]
    public void ProtectLegacyAudioDirectory_DoesNothingWhenLegacyAudioDirectoryIsMissing()
    {
        var legacyAudio = Path.Combine(_root, "TypeWhisper", "Audio");
        var recycle = new FakeRecycleBinOperation(_ => throw new InvalidOperationException("Should not be called."));
        var protector = new UninstallUserDataProtector(recycle, () => DateTimeOffset.UtcNow);

        protector.ProtectLegacyAudioDirectory(legacyAudio, Path.Combine(_root, "recovery"));

        Assert.Empty(recycle.Paths);
    }

    [Fact]
    public void Program_RegistersVelopackBeforeUninstallHook()
    {
        var source = TestFile.ReadProjectFile("src", "TypeWhisper.Windows", "Program.cs");

        Assert.Contains(".OnBeforeUninstallFastCallback", source);
        Assert.Contains("UninstallUserDataProtector.ProtectLegacyAudioDirectory()", source);
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

    private sealed class FakeRecycleBinOperation(Func<string, bool> moveToRecycleBin) : IRecycleBinOperation
    {
        public List<string> Paths { get; } = [];

        public bool TryMoveDirectoryToRecycleBin(string path)
        {
            Paths.Add(path);
            return moveToRecycleBin(path);
        }
    }
}
