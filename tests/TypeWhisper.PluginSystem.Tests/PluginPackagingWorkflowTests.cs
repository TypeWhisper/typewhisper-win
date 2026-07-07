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
}
