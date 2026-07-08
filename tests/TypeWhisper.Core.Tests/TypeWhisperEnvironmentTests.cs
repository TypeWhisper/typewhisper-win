using TypeWhisper.Core;

namespace TypeWhisper.Core.Tests;

public class TypeWhisperEnvironmentTests
{
    [Fact]
    public void AudioPath_IsOutsideVelopackInstallRoot()
    {
        var basePath = Normalize(TypeWhisperEnvironment.BasePath);
        var audioPath = Normalize(TypeWhisperEnvironment.AudioPath);
        var legacyAudioPath = Normalize(TypeWhisperEnvironment.LegacyAudioPath);

        Assert.StartsWith(basePath, legacyAudioPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            IsUnderDirectory(audioPath, basePath),
            $"AudioPath '{audioPath}' must not live under Velopack install root '{basePath}'.");
        Assert.EndsWith(
            Path.Combine(TypeWhisperEnvironment.IsDevelopmentBuild ? "TypeWhisper-DevUserData" : "TypeWhisper-UserData", "Audio"),
            audioPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsUnderDirectory(string path, string directory)
    {
        var normalizedPath = Normalize(path);
        var normalizedDirectory = Normalize(directory);
        return normalizedPath.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
