using TypeWhisper.Core;

namespace TypeWhisper.Core.Tests;

public class TypeWhisperEnvironmentTests
{
    [Fact]
    public void PersistentPaths_AreOutsideVelopackInstallRoot()
    {
        var basePath = Normalize(TypeWhisperEnvironment.BasePath);
        var legacyBasePath = Normalize(TypeWhisperEnvironment.LegacyBasePath);
        var audioPath = Normalize(TypeWhisperEnvironment.AudioPath);
        var legacyAudioPath = Normalize(TypeWhisperEnvironment.LegacyAudioPath);
        var userDataDirectoryName = TypeWhisperEnvironment.IsDevelopmentBuild
            ? "TypeWhisper-DevUserData"
            : "TypeWhisper-UserData";

        Assert.StartsWith(legacyBasePath, legacyAudioPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(IsUnderDirectory(basePath, legacyBasePath));
        Assert.All(
            new[]
            {
                TypeWhisperEnvironment.SettingsFilePath,
                TypeWhisperEnvironment.DataPath,
                TypeWhisperEnvironment.ModelsPath,
                TypeWhisperEnvironment.PluginsPath,
                TypeWhisperEnvironment.PluginDataPath,
                TypeWhisperEnvironment.LogsPath,
                TypeWhisperEnvironment.AudioPath,
                TypeWhisperEnvironment.ApiTokenFilePath
            },
            path => Assert.True(IsUnderDirectory(path, basePath), $"'{path}' must live under '{basePath}'."));
        Assert.EndsWith(
            $"{userDataDirectoryName}{Path.DirectorySeparatorChar}Audio",
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
