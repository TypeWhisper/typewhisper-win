using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;
using Xunit;

public sealed class PluginInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-inventory-" + Guid.NewGuid());
    public PluginInventoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string Package(string folder, string id = "com.test.fixture", string? minimum = null)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var assembly = typeof(ContractProbePlugin).Assembly.Location;
        File.Copy(assembly, Path.Combine(directory, "plugin.dll"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Name = "Fixture", Version = "1.0.0", MinHostVersion = minimum,
            AssemblyName = "plugin.dll", PluginClass = "Does.Not.Exist"
        }));
        return directory;
    }

    [Fact]
    public void DiscoveryDoesNotResolveOrActivateEntryPoint()
    {
        Package("fixture");
        var item = Assert.Single(PortablePluginInventory.Scan(_root, new(1, 1)));
        Assert.Null(item.Error);
        Assert.Equal("com.test.fixture", item.Manifest!.Id);
        Assert.Equal("Does.Not.Exist", item.Manifest.PluginClass);
        Assert.Equal(2, Directory.GetFiles(item.Directory).Length);
    }

    [Fact]
    public void OneMalformedPackageDoesNotHideHealthyPackages()
    {
        Package("healthy");
        var broken = Package("broken", "com.test.broken");
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{broken");
        var items = PortablePluginInventory.Scan(_root, new(1, 1));
        Assert.Equal(2, items.Count);
        Assert.Single(items, item => item.Error is null);
        Assert.Single(items, item => item.Manifest is null && item.Error is not null);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("invalid")]
    public void InvalidOrNewerMinimumHostIsBlocked(string minimum)
    {
        Package("fixture", minimum: minimum);
        Assert.Contains("newer host", Assert.Single(PortablePluginInventory.Scan(_root, new(1, 1))).Error);
    }

    [Fact]
    public void DuplicateIdentitiesAreBothBlocked()
    {
        Package("one"); Package("two");
        Assert.All(PortablePluginInventory.Scan(_root, new(1, 1)), item => Assert.Contains("Duplicate", item.Error));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrInvalidAssemblyRemainsVisibleWithError(bool missing)
    {
        var directory = Package("fixture");
        var path = Path.Combine(directory, "plugin.dll");
        if (missing) File.Delete(path); else File.WriteAllText(path, "not an assembly");
        var item = Assert.Single(PortablePluginInventory.Scan(_root, new(1, 1)));
        Assert.NotNull(item.Manifest);
        Assert.NotNull(item.Error);
    }

    [Fact]
    public void InvalidManifestIdentityDoesNotEscapeDiscovery()
    {
        Package("fixture", "invalid/id");
        Assert.NotNull(Assert.Single(PortablePluginInventory.Scan(_root, new(1, 1))).Error);
    }

    [Fact]
    public void MissingRootIsEmptyWithoutCreatingIt()
    {
        var path = Path.Combine(_root, "missing");
        Assert.Empty(PortablePluginInventory.Scan(path, new(1, 1)));
        Assert.False(Directory.Exists(path));
    }
}
