using System.IO;
using System.Text.Json;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class WhisperCppPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.WhisperCpp", "manifest.json"));
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new WhisperCppPlugin();

        Assert.NotNull(manifest);
        Assert.Equal("1.0.1", manifest.Version);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void BuildNativeLoadFailureMessage_NamesRuntimeFolderAndVcRedistFallback()
    {
        var pluginDirectory = Path.Combine(
            "C:", "Users", "Sal", "AppData", "Local", "TypeWhisper", "Plugins", "com.typewhisper.whisper-cpp");
        var nativeError = new DllNotFoundException(
            "Unable to load DLL 'ggml-cpu-whisper.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)");

        var message = WhisperCppPlugin.BuildNativeLoadFailureMessage(
            pluginDirectory,
            "win-x64",
            nativeError);

        Assert.Contains(Path.Combine(pluginDirectory, "runtimes", "win-x64"), message);
        Assert.Contains("ggml-cpu-whisper.dll", message);
        Assert.Contains("VCOMP140.DLL", message);
        Assert.Contains("Microsoft Visual C++ 2015-2022 Redistributable", message);
        Assert.Contains("0x8007007E", message);
    }
}
