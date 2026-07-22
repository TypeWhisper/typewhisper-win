using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginPackagingWorkflowTests
{
    private static readonly string[] RequiredPortableEntries =
    [
        ".portable",
        "TypeWhisper.exe",
        "Update.exe",
        "current/TypeWhisper.exe"
    ];

    [Theory]
    [InlineData(".github", "workflows", "release.yml")]
    [InlineData(".github", "workflows", "package-dry-run.yml")]
    public void InstallerWorkflows_DoNotBundlePluginsIntoAppPackage(params string[] workflowPath)
    {
        var workflow = TestFile.ReadProjectFile(workflowPath);

        Assert.DoesNotContain("Copy bundled plugins into publish output", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("src/TypeWhisper.Windows/bin/Release/net10.0-windows/Plugins", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("publish/${{ matrix.rid }}/Plugins", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("releases/$env:VELOPACK_CHANNEL", ".github", "workflows", "release.yml")]
    [InlineData("releases/${{ steps.meta.outputs.velopack_channel }}", ".github", "workflows", "package-dry-run.yml")]
    public void InstallerWorkflows_ValidatePortablePackage(
        string expectedReleaseDirectory,
        params string[] workflowPath)
    {
        var workflow = TestFile.ReadProjectFile(workflowPath).ReplaceLineEndings("\n");
        var expectedInvocation =
            "        shell: pwsh\n" +
            "        run: |\n" +
            "          ./eng/Test-PortablePackage.ps1 `\n" +
            $"            -ReleaseDirectory \"{expectedReleaseDirectory}\"";

        Assert.Contains(expectedInvocation, workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortablePackageValidator_AcceptsValidArchive()
    {
        using var releaseDirectory = new TemporaryReleaseDirectory();
        CreatePortableArchive(
            releaseDirectory.Path,
            "TypeWhisper-win-x64-1.0.0-Portable.zip",
            RequiredPortableEntries);

        var result = await RunPortablePackageValidatorAsync(releaseDirectory.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Validated portable package", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortablePackageValidator_RejectsArchiveWithMissingRequiredEntry()
    {
        using var releaseDirectory = new TemporaryReleaseDirectory();
        CreatePortableArchive(
            releaseDirectory.Path,
            "TypeWhisper-win-x64-1.0.0-Portable.zip",
            RequiredPortableEntries.Where(entry => entry != "Update.exe").ToArray());

        var result = await RunPortablePackageValidatorAsync(releaseDirectory.Path);

        AssertPortableValidationFailed(result, "missing required entries: Update.exe");
    }

    [Fact]
    public async Task PortablePackageValidator_RejectsZeroOrMultiplePortableArchives()
    {
        using (var emptyReleaseDirectory = new TemporaryReleaseDirectory())
        {
            var result = await RunPortablePackageValidatorAsync(emptyReleaseDirectory.Path);

            AssertPortableValidationFailed(result, "found 0");
        }

        using (var multipleReleaseDirectory = new TemporaryReleaseDirectory())
        {
            CreatePortableArchive(
                multipleReleaseDirectory.Path,
                "TypeWhisper-win-x64-1.0.0-Portable.zip",
                RequiredPortableEntries);
            CreatePortableArchive(
                multipleReleaseDirectory.Path,
                "TypeWhisper-win-arm64-1.0.0-Portable.zip",
                RequiredPortableEntries);

            var result = await RunPortablePackageValidatorAsync(multipleReleaseDirectory.Path);

            AssertPortableValidationFailed(result, "found 2");
        }
    }

    [Fact]
    public void StorePackageWorkflow_DoesNotBuildBundledPlugins()
    {
        var workflow = TestFile.ReadProjectFile(".github", "workflows", "store-package.yml");

        Assert.DoesNotContain("Build bundled plugins", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem plugins", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build $project.FullName", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Contains("\"-p:Version=$env:VERSION\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Version=${{ env.VERSION }}", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void StorePackageScript_DoesNotCopyPluginsIntoMsixLayout()
    {
        var script = TestFile.ReadProjectFile("eng", "Build-StorePackage.ps1");

        Assert.DoesNotContain("src/TypeWhisper.Windows/bin/$Configuration/net10.0-windows/Plugins", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$pluginsSrc", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -Path $pluginsSrc", script, StringComparison.Ordinal);
        Assert.Contains("$layoutPluginsPath", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $layoutPluginsPath", script, StringComparison.Ordinal);
        Assert.Contains("TypeWhisper-$RuntimeIdentifier-*.msix", script, StringComparison.Ordinal);
        Assert.Contains("must be in the range 0-65535", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginReleaseWorkflow_PublishesPackageHashInRegistry()
    {
        var workflow = TestFile.ReadProjectFile(".github", "workflows", "publish-plugins.yml");

        Assert.Contains("Get-FileHash -Algorithm SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("PLUGIN_SHA256=$zipSha256", workflow, StringComparison.Ordinal);
        Assert.Contains("https://typewhisper.github.io/typewhisper-win/plugins/$env:ZIP_NAME", workflow, StringComparison.Ordinal);
        Assert.Contains("Set-RegistryProperty $registry[$j] 'sha256' $env:PLUGIN_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("sha256 = $env:PLUGIN_SHA256", workflow, StringComparison.Ordinal);
    }

    private static void CreatePortableArchive(
        string releaseDirectory,
        string fileName,
        params string[] entries)
    {
        var archivePath = Path.Join(releaseDirectory, fileName);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (var entry in entries)
            archive.CreateEntry(entry);
    }

    private static async Task<PowerShellResult> RunPortablePackageValidatorAsync(string releaseDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(TestFile.ProjectFile("eng", "Test-PortablePackage.ps1"));
        startInfo.ArgumentList.Add("-ReleaseDirectory");
        startInfo.ArgumentList.Add(releaseDirectory);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Expected PowerShell validator process to start.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new PowerShellResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static void AssertPortableValidationFailed(PowerShellResult result, string expectedMessage)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            expectedMessage,
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
    }

    private sealed record PowerShellResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class TemporaryReleaseDirectory : IDisposable
    {
        public TemporaryReleaseDirectory()
        {
            Path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                $"tw_portable_package_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
