using System.IO;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class CliInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "typewhisper-cli-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildExamples_UseConfiguredPort()
    {
        var cli = CliInstallService.BuildCliExamples(9901);
        var curl = CliInstallService.BuildCurlExamples(9901);

        Assert.Contains(cli, command => command.Contains("--port 9901"));
        Assert.Contains(curl, command => command.Contains("localhost:9901"));
    }

    [Fact]
    public void Install_CopiesBundledCliAndRequestsPathEntry()
    {
        Directory.CreateDirectory(_root);
        var bundledPath = Path.Combine(_root, "typewhisper.exe");
        File.WriteAllText(bundledPath, "fake cli");
        File.WriteAllText(Path.Combine(_root, "typewhisper.dll"), "fake dll");
        File.WriteAllText(Path.Combine(_root, "typewhisper.runtimeconfig.json"), "{}");
        var installDirectory = Path.Combine(_root, "install");
        var pathUpdates = new List<string>();
        var service = new CliInstallService(
            () => bundledPath,
            () => installDirectory,
            pathUpdates.Add);

        var state = service.Install();

        Assert.True(state.Installed);
        Assert.Equal(Path.Combine(installDirectory, "typewhisper.exe"), state.InstallPath);
        Assert.Equal("fake cli", File.ReadAllText(state.InstallPath));
        Assert.True(File.Exists(Path.Combine(installDirectory, "typewhisper.dll")));
        Assert.True(File.Exists(Path.Combine(installDirectory, "typewhisper.runtimeconfig.json")));
        Assert.Equal([installDirectory], pathUpdates);
    }

    [Fact]
    public void GetState_ReportsMissingBundledCli()
    {
        var service = new CliInstallService(
            () => null,
            () => Path.Combine(_root, "install"),
            _ => { });

        var state = service.GetState();

        Assert.False(state.BundledCliAvailable);
        Assert.False(state.Installed);
        Assert.Contains("not found", state.StatusText);
    }

    [Fact]
    public void GetState_IgnoresAppExeWithDifferentCasing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "TypeWhisper.exe"), "app exe");
        var service = new CliInstallService(
            () => null,
            () => _root,
            _ => { });

        var state = service.GetState();

        Assert.False(state.Installed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
