using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiCompatiblePluginTests
{
    [Fact]
    public void Manifest_AdvertisesTranscriptionAndLlmCategories()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAiCompatible", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(manifest);
        Assert.Equal("transcription", manifest.Category);
        Assert.Equal(["transcription", "llm"], manifest.Categories);
    }
}
