namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginPackagingWorkflowTests
{
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
    [InlineData(".github", "workflows", "release.yml")]
    [InlineData(".github", "workflows", "package-dry-run.yml")]
    public void InstallerWorkflows_ValidatePortablePackage(params string[] workflowPath)
    {
        var workflow = TestFile.ReadProjectFile(workflowPath);

        Assert.Contains("eng/Test-PortablePackage.ps1", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PortablePackageValidator_RequiresVelopackRuntimeLayout()
    {
        var script = TestFile.ReadProjectFile("eng", "Test-PortablePackage.ps1");

        Assert.Contains("*-Portable.zip", script, StringComparison.Ordinal);
        Assert.Contains(".portable", script, StringComparison.Ordinal);
        Assert.Contains("TypeWhisper.exe", script, StringComparison.Ordinal);
        Assert.Contains("Update.exe", script, StringComparison.Ordinal);
        Assert.Contains("current/TypeWhisper.exe", script, StringComparison.Ordinal);
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
}
