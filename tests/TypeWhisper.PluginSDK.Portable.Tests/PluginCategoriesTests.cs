using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class PluginCategoriesTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CatalogAndManifestPreserveMultipleCategories()
    {
        const string metadata = """
            {"id":"com.example.provider","name":"Provider","version":"1.1.0",
             "categories":["transcription","llm"],"downloadUrl":"https://example.com/plugin.zip",
             "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":100,
             "assemblyName":"plugin.dll","pluginClass":"Provider"}
            """;
        var entry = JsonSerializer.Deserialize<PortableCatalogEntry>(metadata, Json)!;
        entry.Validate();
        Assert.Equal(new[] { "transcription", "llm" }, entry.Categories);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(metadata, Json)!;
        Assert.Equal(entry.Categories, manifest.Categories);
        Assert.DoesNotContain("\"category\":", JsonSerializer.Serialize(entry, Json));
        Assert.DoesNotContain("\"category\":", JsonSerializer.Serialize(manifest, Json));
    }

    [Fact]
    public void SingularCategoryIsNotMigrated()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>("""
            {"id":"com.example.provider","name":"Provider","version":"1.1.0",
             "category":"llm","assemblyName":"plugin.dll","pluginClass":"Provider"}
            """, Json)!;
        Assert.Null(manifest.Categories);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("all")]
    public void InvalidFilterIdentifiersAreRejected(string id)
    {
        var entry = new PortableCatalogEntry
        {
            Id = "com.example.provider", Name = "Provider", Version = "1.1.0",
            DownloadUrl = "https://example.com/plugin.zip", Sha256 = new string('a', 64),
            Size = 100, Categories = [id]
        };
        Assert.Throws<InvalidDataException>(entry.Validate);
    }
}
