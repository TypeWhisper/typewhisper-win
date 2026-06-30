using System.Text.Json;
using TypeWhisper.Plugin.GraniteSpeech;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GraniteSpeechPluginTests
{
    [Fact]
    public void Manifest_TargetsTypeWhisper084OrNewer()
    {
        var manifest = ReadManifest();

        Assert.NotNull(manifest);
        Assert.Equal("com.typewhisper.granite-speech", manifest.Id);
        Assert.Equal("0.8.4", manifest.MinHostVersion);
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = ReadManifest();
        var sut = new GraniteSpeechPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    private static PluginManifest? ReadManifest() =>
        JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile("plugins", "TypeWhisper.Plugin.GraniteSpeech", "manifest.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
}
