using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ApplicationActivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRequestIsConsumedAndLaterRequestsContinueEvenIfReportingFails(bool reportingFails)
    {
        var inbox = new ActivationInbox();
        inbox.Add(ApplicationActivationRequest.Parse(["--settings"]));
        inbox.Add(ApplicationActivationRequest.Parse(["--files"]));
        var routes = new List<string?>();
        var reported = 0;
        inbox.Dispatch(request =>
        {
            routes.Add(request.Route);
            if (request.Route == "--settings") throw new InvalidOperationException("View failed");
        }, error =>
        {
            reported++; Assert.Equal("View failed", error.Message);
            if (reportingFails) throw new InvalidOperationException("Error view failed");
        });
        Assert.Equal(new[] { "--settings", "--files" }, routes);
        Assert.Equal(1, reported);
        Assert.Empty(inbox.Drain());
    }
    [Fact]
    public void RedirectAndInitialArgumentsProduceTheSameFileRequest()
    {
        var initial = ApplicationActivationRequest.Parse(["--transcribe-file", @"C:\My audio\Übung.wav", @"\\server\share\two.wav"]);
        var redirected = ApplicationActivationRequest.ParseCommandLine("\"C:\\Program Files\\TypeWhisper.exe\" --transcribe-file \"C:\\My audio\\Übung.wav\" \\\\server\\share\\two.wav");
        Assert.Null(initial.Error); Assert.Null(redirected.Error);
        Assert.Equal(initial.Route, redirected.Route);
        Assert.Equal(initial.Files, redirected.Files);
        Assert.True(redirected.ShowWindow);
    }

    [Fact]
    public void WindowsTrailingBackslashBeforeClosingQuoteIsPreserved()
    {
        var request = ApplicationActivationRequest.ParseCommandLine("app.exe --transcribe-file \"C:\\audio\\\\\"");
        Assert.Equal(@"C:\audio\", Assert.Single(request.Files));
    }

    [Theory]
    [InlineData("app.exe --transcribe-file")]
    [InlineData("app.exe --transcribe-file relative.wav")]
    [InlineData("app.exe --transcribe-file C:relative.wav")]
    [InlineData("app.exe --unknown")]
    [InlineData("app.exe --settings --files")]
    public void InvalidActivationsHaveVisibleErrorsAndNoPartialFiles(string command)
    {
        var request = ApplicationActivationRequest.ParseCommandLine(command);
        Assert.NotNull(request.Error); Assert.True(request.ShowWindow); Assert.Empty(request.Files);
    }

    [Fact]
    public void NavigationOverridesMinimizedAndStartupIsOtherwiseSilent()
    {
        Assert.False(ApplicationActivationRequest.Parse([], true).ShowWindow);
        Assert.False(ApplicationActivationRequest.Parse(["--minimized"]).ShowWindow);
        var explicitRequest = ApplicationActivationRequest.ParseCommandLine("app.exe --minimized --settings", true);
        Assert.True(explicitRequest.ShowWindow); Assert.Equal("--settings", explicitRequest.Route);
    }

    [Fact]
    public void FileAndPendingRequestLimitsRejectExcessWithoutTruncatingSilently()
    {
        var files = Enumerable.Range(0, 21).Select(i => $@"C:\audio\{i}.wav");
        Assert.NotNull(ApplicationActivationRequest.Parse(new[] { "--transcribe-file" }.Concat(files)).Error);
        var inbox = new ActivationInbox();
        for (var i = 0; i < 10; i++) inbox.Add(ApplicationActivationRequest.Parse(["--files"]));
        var drained = inbox.Drain();
        Assert.Equal(9, drained.Count); Assert.NotNull(drained[^1].Error);
        Assert.Empty(inbox.Drain());
        inbox.Close(); inbox.Add(ApplicationActivationRequest.Parse(["--files"]));
        Assert.Empty(inbox.Drain());
    }

    [Fact]
    public void AddingAFileLeavesTheExistingQueueIdleWithoutAResult()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var queue = new FileTranscriptionQueue();
            Assert.Null(queue.Add(path));
            Assert.False(queue.Running);
            Assert.Equal(FileTranscriptionStatus.Queued, Assert.Single(queue.Jobs).Status);
            Assert.Null(queue.Jobs[0].Result);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
