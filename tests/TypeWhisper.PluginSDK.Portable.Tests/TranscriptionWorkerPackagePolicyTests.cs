using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class TranscriptionWorkerPackagePolicyTests : IDisposable
{
    private const string PluginId = "com.typewhisper.whisper-cpp";
    private readonly string _root = Path.Join(Path.GetTempPath(), "worker-policy-" + Guid.NewGuid().ToString("N"));
    private string Installed => Path.Join(_root, "PluginPackages", "packages");
    private string Bundled => Path.Join(_root, "Plugins");
    private TranscriptionWorkerPackagePolicy Policy() => new(new HashSet<string> { PluginId }, Installed, Bundled);

    public void Dispose() => TryDelete(_root);

    // Removing the temporary folder is best effort; a file still in use must not fail the test.
    private static bool TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string WritePackage(string directory, string id = PluginId)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Name = "Fixture", Version = "1.0.0", IsLocal = true, AssemblyName = "fixture.dll", PluginClass = "Fixture.Plugin"
        }));
        return directory;
    }

    [Fact]
    public void AcceptsInstalledAndBundledPackagesOfIsolatedPlugins()
    {
        var installed = WritePackage(Path.Join(Installed, Guid.NewGuid().ToString("N")));
        var bundled = WritePackage(Path.Join(Bundled, PluginId));
        Assert.Null(Policy().Refusal(installed, PluginId));
        Assert.Null(Policy().Refusal(installed + Path.DirectorySeparatorChar, PluginId));
        Assert.Null(Policy().Refusal(bundled, PluginId));
        Assert.Null(new TranscriptionWorkerPackagePolicy(new HashSet<string> { PluginId }, Installed).Refusal(installed, PluginId));
    }

    [Fact]
    public void RefusesOtherPluginsLocationsAndManifests()
    {
        var installed = WritePackage(Path.Join(Installed, Guid.NewGuid().ToString("N")));
        Assert.NotNull(Policy().Refusal(installed, "com.typewhisper.other"));
        Assert.NotNull(Policy().Refusal(installed, ""));
        Assert.NotNull(Policy().Refusal(WritePackage(Path.Join(_root, "elsewhere", PluginId)), PluginId));
        Assert.NotNull(Policy().Refusal(WritePackage(Path.Join(Installed, "not-a-guid")), PluginId));
        Assert.NotNull(Policy().Refusal(WritePackage(Path.Join(Installed, Guid.NewGuid().ToString("N"), "nested")), PluginId));
        Assert.NotNull(Policy().Refusal(WritePackage(Path.Join(Bundled, "com.typewhisper.other")), PluginId));
        Assert.NotNull(Policy().Refusal(Path.Join(Installed, Guid.NewGuid().ToString("N")), PluginId));
        // The path is normalized before the comparison, so a detour through ".." ends outside the folders.
        Assert.NotNull(Policy().Refusal(Path.Join(Installed, "..", "..", "elsewhere", PluginId), PluginId));
        Assert.NotNull(Policy().Refusal("", PluginId));
        Assert.NotNull(new TranscriptionWorkerPackagePolicy(new HashSet<string> { PluginId }, Installed)
            .Refusal(WritePackage(Path.Join(Bundled, PluginId)), PluginId));

        var mismatch = WritePackage(Path.Join(Installed, Guid.NewGuid().ToString("N")), "com.typewhisper.other");
        Assert.Contains("expected plugin", Policy().Refusal(mismatch, PluginId));
        var unreadable = Path.Join(Installed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unreadable);
        Assert.NotNull(Policy().Refusal(unreadable, PluginId));
    }

    [Fact]
    public void RefusesAJunctionWithAnExpectedName()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = WritePackage(Path.Join(_root, "elsewhere", PluginId));
        Directory.CreateDirectory(Installed);
        var junction = Path.Join(Installed, Guid.NewGuid().ToString("N"));
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, target },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        try
        {
            Assert.True(File.Exists(Path.Join(junction, "manifest.json")));
            Assert.NotNull(Policy().Refusal(junction, PluginId));
        }
        finally { Directory.Delete(junction); }
    }
}
