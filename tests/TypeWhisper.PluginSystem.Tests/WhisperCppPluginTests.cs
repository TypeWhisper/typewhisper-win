using System.IO;
using System.Text.Json;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.PluginSystem.Tests;

public class WhisperCppPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var repoRoot = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifestPath = Path.Join(
            repoRoot,
            "plugins",
            "TypeWhisper.Plugin.WhisperCpp",
            "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new WhisperCppPlugin();

        Assert.NotNull(manifest);
        Assert.Equal("1.0.1", manifest.Version);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, RuntimeLibrary.Cuda, RuntimeLibrary.Cpu)]
    [InlineData(TranscriptionAccelerationPreference.Cpu, RuntimeLibrary.Cpu)]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, RuntimeLibrary.Cuda)]
    public void GetRuntimeLibraryOrder_MapsAccelerationPreference(
        TranscriptionAccelerationPreference preference,
        params RuntimeLibrary[] expectedOrder)
    {
        var order = WhisperCppPlugin.GetRuntimeLibraryOrder(preference);

        Assert.Equal(expectedOrder, order);
    }

    [Fact]
    public void CudaRuntimePackage_IsReferencedAndRequiredInPluginOutput()
    {
        var repoRoot = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var projectPath = Path.Join(
            repoRoot,
            "plugins",
            "TypeWhisper.Plugin.WhisperCpp",
            "TypeWhisper.Plugin.WhisperCpp.csproj");

        var project = File.ReadAllText(projectPath);

        Assert.Contains("Whisper.net.Runtime.Cuda.Windows", project);
        Assert.Contains("ggml-cuda-whisper.dll", project);
    }

    [Fact]
    public void BuildNativeLoadFailureMessage_NamesRuntimeFolderAndVcRedistFallback()
    {
        var pluginDirectory = Path.Join(
            @"C:\", "Users", "Sal", "AppData", "Local", "TypeWhisper", "Plugins", "com.typewhisper.whisper-cpp");
        var nativeError = new DllNotFoundException(
            "Unable to load DLL 'ggml-cpu-whisper.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)");

        var message = WhisperCppPlugin.BuildNativeLoadFailureMessage(
            pluginDirectory,
            "win-x64",
            nativeError);

        Assert.Contains(Path.Join(pluginDirectory, "runtimes", "win-x64"), message);
        Assert.Contains("ggml-cpu-whisper.dll", message);
        Assert.Contains("VCOMP140.DLL", message);
        Assert.Contains("Microsoft Visual C++ 2015-2022 Redistributable", message);
        Assert.Contains("0x8007007E", message);
    }

    [Fact]
    public void BuildNativeLoadFailureMessage_StripsPathLikeRuntimeIdentifier()
    {
        var pluginDirectory = Path.Join(
            @"C:\", "Users", "Sal", "AppData", "Local", "TypeWhisper", "Plugins", "com.typewhisper.whisper-cpp");
        var nativeError = new DllNotFoundException("Unable to load DLL 'ggml-cpu-whisper.dll'.");

        var message = WhisperCppPlugin.BuildNativeLoadFailureMessage(
            pluginDirectory,
            Path.Join("unexpected", "win-x64"),
            nativeError);

        Assert.Contains(Path.Join(pluginDirectory, "runtimes", "win-x64"), message);
        Assert.DoesNotContain(Path.Join(pluginDirectory, "runtimes", "unexpected"), message);
    }
}
