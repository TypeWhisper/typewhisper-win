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

    [Fact]
    public void StorePackageWorkflow_DoesNotBuildBundledPlugins()
    {
        var workflow = TestFile.ReadProjectFile(".github", "workflows", "store-package.yml");

        Assert.DoesNotContain("Build bundled plugins", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem plugins", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build $project.FullName", workflow, StringComparison.Ordinal);
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
    }
}
