using System.IO;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class UiAutomationLaunchOptionsTests : IDisposable
{
    private readonly string _tempDirectory = Path.Join(
        Path.GetTempPath(),
        $"tw_ui_options_{Guid.NewGuid():N}");

    public UiAutomationLaunchOptionsTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    [Fact]
    public void TryParse_LeavesNormalLaunchDisabled()
    {
        var parsed = UiAutomationLaunchOptions.TryParse([], out var options, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.False(options.IsEnabled);
    }

    [Fact]
    public void TryParse_RequiresAnIsolatedDataRoot()
    {
        var parsed = UiAutomationLaunchOptions.TryParse(
            ["--ui-automation"],
            out _,
            out var error);

        Assert.False(parsed);
#if DEBUG
        Assert.Contains("--automation-data-root", error, StringComparison.Ordinal);
#else
        Assert.Contains("available only in debug builds", error, StringComparison.Ordinal);
#endif
    }

    [Fact]
    public void TryParse_ResolvesFixtureOptions()
    {
        var registryFile = Path.Join(_tempDirectory, "plugins.json");
        File.WriteAllText(registryFile, "[]");
        var readyFile = Path.Join(_tempDirectory, "ready.json");

        var parsed = UiAutomationLaunchOptions.TryParse(
            [
                "--ui-automation",
                "--automation-data-root", _tempDirectory,
                "--automation-language", "de",
                "--automation-display-version", "1.2.3",
                "--automation-instance", "screenshots-1",
                "--automation-ready-file", readyFile,
                "--automation-plugin-registry", registryFile,
                "--automation-premium"
            ],
            out var options,
            out var error);

#if DEBUG
        Assert.True(parsed);
        Assert.Null(error);
        Assert.True(options.IsEnabled);
        Assert.True(options.HasPremiumFixture);
        Assert.Equal("de", options.Language);
        Assert.Equal("1.2.3", options.DisplayVersion);
        Assert.Equal(
            new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc),
            options.ReferenceUtc);
        Assert.Equal("screenshots-1", options.InstanceId);
        Assert.Equal(Path.GetFullPath(_tempDirectory), options.DataRoot);
        Assert.Equal(Path.GetFullPath(readyFile), options.ReadyFile);
        Assert.Equal(Path.GetFullPath(registryFile), options.PluginRegistryFile);
#else
        Assert.False(parsed);
        Assert.Contains("available only in debug builds", error, StringComparison.Ordinal);
        Assert.False(options.IsEnabled);
#endif
    }

#if DEBUG
    [Fact]
    public void TryParse_RejectsExplicitlyEmptyInstanceId()
    {
        var parsed = UiAutomationLaunchOptions.TryParse(
            [
                "--ui-automation",
                "--automation-data-root", _tempDirectory,
                "--automation-instance", ""
            ],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Contains("instance identifier", error, StringComparison.Ordinal);
    }
#endif
}
