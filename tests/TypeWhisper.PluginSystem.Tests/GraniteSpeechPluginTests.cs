using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GraniteSpeechPluginTests
{
    [Fact]
    public void Manifest_TargetsTypeWhisper084OrNewer()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile("plugins", "TypeWhisper.Plugin.GraniteSpeech", "manifest.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Equal("com.typewhisper.granite-speech", manifest.Id);
        Assert.Equal("0.8.4", manifest.MinHostVersion);
    }
}
