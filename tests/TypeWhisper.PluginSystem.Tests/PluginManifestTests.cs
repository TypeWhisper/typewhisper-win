using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginManifestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialize_AllFields()
    {
        var json = """
        {
            "id": "com.example.test",
            "name": "Test Plugin",
            "version": "2.1.0",
            "minHostVersion": "1.0.0",
            "author": "Test Author",
            "description": "A test plugin for unit tests",
            "assemblyName": "TestPlugin.dll",
            "pluginClass": "TestPlugin.MyPlugin"
        }
        """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.test", manifest.Id);
        Assert.Equal("Test Plugin", manifest.Name);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Equal("1.0.0", manifest.MinHostVersion);
        Assert.Equal("Test Author", manifest.Author);
        Assert.Equal("A test plugin for unit tests", manifest.Description);
        Assert.Equal("TestPlugin.dll", manifest.AssemblyName);
        Assert.Equal("TestPlugin.MyPlugin", manifest.PluginClass);
    }

    [Fact]
    public void Deserialize_OnlyRequiredFields()
    {
        var json = """
        {
            "id": "com.example.minimal",
            "name": "Minimal",
            "version": "1.0.0",
            "assemblyName": "Minimal.dll",
            "pluginClass": "Minimal.Plugin"
        }
        """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.minimal", manifest.Id);
        Assert.Equal("Minimal", manifest.Name);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("Minimal.dll", manifest.AssemblyName);
        Assert.Equal("Minimal.Plugin", manifest.PluginClass);
        Assert.Null(manifest.MinHostVersion);
        Assert.Null(manifest.Author);
        Assert.Null(manifest.Description);
    }

    [Fact]
    public void Serialize_RoundTrip()
    {
        var original = new PluginManifest
        {
            Id = "com.example.roundtrip",
            Name = "Roundtrip",
            Version = "3.0.0",
            Author = "Me",
            Description = "Test roundtrip",
            AssemblyName = "RT.dll",
            PluginClass = "RT.Plugin",
            MinHostVersion = "2.0.0"
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Author, deserialized.Author);
        Assert.Equal(original.Description, deserialized.Description);
        Assert.Equal(original.AssemblyName, deserialized.AssemblyName);
        Assert.Equal(original.PluginClass, deserialized.PluginClass);
        Assert.Equal(original.MinHostVersion, deserialized.MinHostVersion);
    }

    [Fact]
    public void Deserialize_CaseInsensitive()
    {
        var json = """
        {
            "ID": "com.example.case",
            "NAME": "Case Test",
            "VERSION": "1.0.0",
            "ASSEMBLYNAME": "Case.dll",
            "PLUGINCLASS": "Case.Plugin"
        }
        """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.case", manifest.Id);
        Assert.Equal("Case Test", manifest.Name);
    }

    [Fact]
    public void Deserialize_ExtraFields_AreIgnored()
    {
        var json = """
        {
            "id": "com.example.extra",
            "name": "Extra",
            "version": "1.0.0",
            "assemblyName": "Extra.dll",
            "pluginClass": "Extra.Plugin",
            "someUnknownField": "value",
            "anotherField": 42
        }
        """;

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.extra", manifest.Id);
    }

    [Fact]
    public void Record_Equality()
    {
        var a = new PluginManifest
        {
            Id = "com.example.eq",
            Name = "Eq",
            Version = "1.0.0",
            AssemblyName = "Eq.dll",
            PluginClass = "Eq.Plugin"
        };

        var b = new PluginManifest
        {
            Id = "com.example.eq",
            Name = "Eq",
            Version = "1.0.0",
            AssemblyName = "Eq.dll",
            PluginClass = "Eq.Plugin"
        };

        Assert.Equal(a, b);
    }

    [Fact]
    public void Record_With_CreatesModifiedCopy()
    {
        var original = new PluginManifest
        {
            Id = "com.example.with",
            Name = "Original",
            Version = "1.0.0",
            AssemblyName = "With.dll",
            PluginClass = "With.Plugin"
        };

        var modified = original with { Name = "Modified", Version = "2.0.0" };

        Assert.Equal("com.example.with", modified.Id);
        Assert.Equal("Modified", modified.Name);
        Assert.Equal("2.0.0", modified.Version);
        Assert.NotEqual(original, modified);
    }
}
