using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;

public sealed class PluginLocalityTests
{
    private static PluginManifest Manifest(string json) => JsonSerializer.Deserialize<PluginManifest>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private const string Required = "\"id\":\"com.typewhisper.x\",\"name\":\"X\",\"version\":\"1.0.0\",\"assemblyName\":\"x.dll\",\"pluginClass\":\"X\"";

    [Theory]
    [InlineData("\"locality\":\"local\"", PluginLocality.Local)]
    [InlineData("\"locality\":\" Cloud \"", PluginLocality.Cloud)]
    [InlineData("\"locality\":\"configurable\",\"isLocal\":true", PluginLocality.Configurable)]
    [InlineData("\"locality\":\"bogus\",\"isLocal\":true", PluginLocality.Unknown)]
    [InlineData("\"isLocal\":true", PluginLocality.Local)]
    [InlineData("\"isLocal\":false", PluginLocality.Cloud)]
    [InlineData("", PluginLocality.Cloud)]
    public void ManifestLocality_PrefersTheFieldAndFallsBackToIsLocal(string extra, PluginLocality expected)
    {
        var manifest = Manifest("{" + Required + (extra.Length == 0 ? "" : "," + extra) + "}");

        Assert.Equal(expected, manifest.ResolvedLocality);
    }

    [Fact]
    public void ManifestLocality_IsNotSerializedBack()
    {
        var json = JsonSerializer.Serialize(Manifest("{" + Required + ",\"locality\":\"local\"}"));

        Assert.Contains("\"Locality\":\"local\"", json);
        Assert.DoesNotContain("ResolvedLocality", json);
    }

    [Fact]
    public void CatalogEntry_AcceptsKnownLocalityAndRejectsUnknown()
    {
        PortableCatalogEntry Entry(string? locality) => new()
        {
            Id = "com.typewhisper.x", Name = "X", Version = "1.0.0", DownloadUrl = "https://packages.test/x.zip",
            Sha256 = new string('a', 64), Size = 10, Categories = ["llm"], Locality = locality
        };

        Entry(null).Validate();
        Entry("cloud").Validate();
        Assert.Equal(PluginLocality.Configurable, Entry("configurable").ResolvedLocality);
        Assert.Equal(PluginLocality.Unknown, Entry(null).ResolvedLocality);
        Assert.Throws<InvalidDataException>(() => Entry("bogus").Validate());
    }

    [Fact]
    public void EveryShippedManifestDeclaresAKnownLocality()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TypeWhisper.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var manifests = Directory.GetFiles(Path.Combine(root.FullName, "plugins"), "manifest.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToArray();

        Assert.NotEmpty(manifests);
        foreach (var path in manifests)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(document.RootElement.TryGetProperty("locality", out var locality), path);
            Assert.NotEqual(PluginLocality.Unknown, PluginLocalityValues.Parse(locality.GetString()));
        }
    }
}
