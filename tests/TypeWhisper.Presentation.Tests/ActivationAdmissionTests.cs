using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ActivationAdmissionTests
{
    [Theory]
    [InlineData("--files")]
    [InlineData("--dictionary")]
    [InlineData("--snippets")]
    [InlineData("--setup")]
    public void OpenWorkspaceRejectsExternalNavigationWithoutCallingItsMutation(string route)
    {
        var draft = "Unsaved original draft";
        var rejection = ActivationAdmission.Reject(false, true, false, true, route);
        if (rejection is null) draft = "Discarded by Present";
        Assert.NotNull(rejection);
        Assert.Equal("Unsaved original draft", draft);
    }

    [Fact]
    public void SameFilesOnlyAcceptsAnIdleListAndNeverAnotherDestination()
    {
        Assert.Null(ActivationAdmission.Reject(false, true, true, true, "--files"));
        Assert.NotNull(ActivationAdmission.Reject(false, true, true, false, "--files"));
        Assert.NotNull(ActivationAdmission.Reject(false, true, true, true, "--dictionary"));
        Assert.NotNull(ActivationAdmission.Reject(true, true, true, true, "--files"));
    }

    [Fact]
    public void QuickLaunchAllowsNavigationAndPlainActivationDoesNotReplaceWorkspace()
    {
        Assert.Null(ActivationAdmission.Reject(false, false, false, false, "--files"));
        Assert.Null(ActivationAdmission.Reject(false, true, false, false, null));
        Assert.NotNull(ActivationAdmission.Reject(true, false, false, false, "--files"));
    }
}
