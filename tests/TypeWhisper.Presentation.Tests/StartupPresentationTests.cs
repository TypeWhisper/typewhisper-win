using Xunit;

namespace TypeWhisper.Presentation.Tests;

public class StartupPresentationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PlainAndAutostartLaunchesUseSetupProgress(bool completed, bool autostart)
    {
        Assert.Equal(completed ? StartupPresentation.Tray : StartupPresentation.Setup,
            StartupPresentationPolicy.Resolve(ApplicationActivationRequest.Parse([], autostart), completed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinimizedLaunchStillResumesUnfinishedSetup(bool completed)
    {
        Assert.Equal(completed ? StartupPresentation.Tray : StartupPresentation.Setup,
            StartupPresentationPolicy.Resolve(ApplicationActivationRequest.Parse(["--minimized"]), completed));
    }

    [Theory]
    [InlineData("--setup")]
    [InlineData("--settings")]
    [InlineData("--files")]
    [InlineData("--unknown")]
    public void ExplicitRoutesAndErrorsAreNotHidden(string argument)
    {
        foreach (var completed in new[] { true, false })
            Assert.Equal(StartupPresentation.RequestedDestination,
                StartupPresentationPolicy.Resolve(ApplicationActivationRequest.Parse([argument]), completed));
    }

    [Fact]
    public void FileAndAccountActivationsArePreserved()
    {
        var file = ApplicationActivationRequest.Parse(["--transcribe-file", @"C:\audio.wav"]);
        var callback = new ApplicationActivationRequest(null, [], null, true) { AccountCallback = new Uri("typewhisper://callback") };
        Assert.Equal(StartupPresentation.RequestedDestination, StartupPresentationPolicy.Resolve(file, true));
        Assert.Equal(StartupPresentation.RequestedDestination, StartupPresentationPolicy.Resolve(callback, true));
    }

    [Fact]
    public void InitialLaunchPolicyDoesNotSuppressSubsequentOpenRequests()
    {
        var request = ApplicationActivationRequest.Parse([]);
        Assert.Equal(StartupPresentation.Tray, StartupPresentationPolicy.Resolve(request, true));
        Assert.True(request.ShowWindow);
    }
}
