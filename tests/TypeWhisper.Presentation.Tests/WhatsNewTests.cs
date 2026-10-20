using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WhatsNewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "whats-new-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_directory, "whats-new.json");
    private WhatsNewPreferencesStore Store() => new(StorePath);
    private WhatsNewSession Session(bool completed = true, int version = 1, ApplicationActivationRequest? request = null,
        bool afterUpdate = false) =>
        new(Store(), request ?? ApplicationActivationRequest.Parse([]), completed, version, afterUpdate);

    [Fact]
    public void ExistingUserSeesContentOnceAcrossCloseAndRestart()
    {
        var session = Session();
        Assert.False(File.Exists(StorePath));
        var shows = 0;
        Assert.True(session.TryShow(false, () => shows++));
        Assert.False(session.TryShow(false, () => shows++));
        Assert.False(Session().TryShow(false, () => shows++));
        Assert.Equal(1, shows);
        Assert.Equal(1, Store().LastSeenContentVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshSetupBaselinesContentWithoutAnExtraTour(bool autostart)
    {
        Assert.False(Session(completed: false, version: 3, request: ApplicationActivationRequest.Parse([], autostart))
            .TryShow(false, UnexpectedShow));
        Assert.Equal(3, Store().LastSeenContentVersion);
        Assert.False(Session(version: 3).TryShow(false, UnexpectedShow));
        Assert.True(Session(version: 4).TryShow(false, () => { }));
    }

    [Fact]
    public void EachNewContentVersionAppearsOnceAndDowngradesDoNotRewindProgress()
    {
        Assert.True(Session(version: 2).TryShow(false, () => { }));
        Assert.False(Session(version: 1).TryShow(false, UnexpectedShow));
        Assert.False(Session(completed: false, version: 1).TryShow(false, UnexpectedShow));
        Assert.Equal(2, Store().LastSeenContentVersion);
        Assert.True(Session(version: 5).TryShow(false, () => { }));
        Assert.False(Session(version: 5).TryShow(false, UnexpectedShow));
        Assert.Equal(5, Store().LastSeenContentVersion);
    }

    [Fact]
    public void PreviouslySeenContentCanBeReopenedWithoutRearmingAutomaticPresentation()
    {
        Assert.True(Session().TryShow(false, () => { }));
        var session = Session();
        Assert.False(session.Pending);
        var shows = 0;
        Assert.True(session.TryShowManually(false, () => shows++));
        Assert.True(session.TryShowManually(false, () => shows++));
        Assert.Equal(2, shows);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(Session().TryShow(false, UnexpectedShow));
        Assert.Equal(1, Store().LastSeenContentVersion);
    }

    [Fact]
    public void ExplicitViewingFromSettingsMarksUnseenContentWithoutWaitingForANormalLaunch()
    {
        var session = Session(request: ApplicationActivationRequest.Parse(["--settings"]));
        Assert.False(session.Pending);
        Assert.True(session.TryShowManually(false, () => { }));
        Assert.Equal(1, Store().LastSeenContentVersion);
        Assert.False(Session().TryShow(false, UnexpectedShow));
    }

    [Fact]
    public void BusyManualRequestPreservesUnseenContentAndRequiresAnotherExplicitRequest()
    {
        var session = Session();
        Assert.False(session.TryShowManually(true, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.True(session.TryShowManually(false, () => { }));
        Assert.Equal(1, Store().LastSeenContentVersion);
    }

    [Fact]
    public void UnreadablePreferencesDoNotBlockExplicitViewingOrReplaceDamagedData()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, "damaged preferences");
        var session = Session();
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.True(session.TryShowManually(false, () => { }));
        Assert.NotNull(session.Error);
        Assert.Equal("damaged preferences", File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData("--settings")]
    [InlineData("--setup")]
    [InlineData("--files")]
    [InlineData("--dictionary")]
    [InlineData("--page", "recorder")]
    [InlineData("--transcribe-file", @"C:\audio.wav")]
    [InlineData("--unknown")]
    [InlineData("--minimized")]
    [InlineData("-a", "--minimized")]
    [InlineData("typewhisper://invalid")]
    public void ExplicitDestinationsAndBackgroundLaunchesLeaveNewsForAnotherStart(params string[] arguments)
    {
        Assert.False(Session(request: ApplicationActivationRequest.Parse(arguments)).TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
        Assert.True(Session().TryShow(false, () => { }));
    }

    [Fact]
    public void AutostartAndAccountCallbacksNeverShowNews()
    {
        Assert.False(Session(request: ApplicationActivationRequest.Parse([], startup: true)).TryShow(false, UnexpectedShow));
        var callback = new ApplicationActivationRequest(null, [], null, true) { AccountCallback = new Uri("typewhisper://callback") };
        Assert.False(Session(request: callback).TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task RedirectOrShortcutDuringInitializationCancelsEvenIfTheActionAlreadyFinished()
    {
        var session = Session();
        // The activation/keyboard thread latches intent before its UI callback can run.
        Assert.True(session.Pending);
        await Task.Run(session.Suppress);
        Assert.False(session.Pending);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
        Assert.True(Session().TryShow(false, () => { }));
    }

    [Fact]
    public void BusyAtPresentationIsNotRetriedWhenRecordingOrProcessingEnds()
    {
        var session = Session();
        Assert.False(session.TryShow(true, UnexpectedShow));
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void FailedPresentationDoesNotAutomaticallyRetryAcrossSessions()
    {
        var session = Session();
        Assert.Throws<InvalidOperationException>(() => session.TryShow(false, () => throw new InvalidOperationException("Window unavailable")));
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.Equal(1, Store().LastSeenContentVersion);
        Assert.False(Session().TryShow(false, UnexpectedShow));
        Assert.True(Session().TryShowManually(false, () => { }));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"LastSeenContentVersion\":-1}")]
    [InlineData("{\"LastSeenContentVersion\":1.5}")]
    [InlineData("{\"LastSeenContentVersion\":\"1\"}")]
    [InlineData("{\"LastSeenContentVersion\":1,\"LastSeenContentVersion\":0}")]
    [InlineData("{\"LastSeenContentVersion\":1,\"FutureProperty\":true}")]
    public void UnreadableProgressSuppressesOptionalNewsAndPreservesTheFile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, json);
        Assert.NotNull(Store().Error);
        Assert.False(Session().TryShow(false, UnexpectedShow));
        Assert.False(Session(completed: false).TryShow(false, UnexpectedShow));
        Assert.Equal(json, File.ReadAllText(StorePath));
    }

    [Fact]
    public void FailedSaveKeepsPreviousVersionAndSuppressesAutomaticPresentation()
    {
        var store = Store();
        store.RecordSeen(1);
        var session = new WhatsNewSession(store, ApplicationActivationRequest.Parse([]), true, 2);
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.NotNull(session.Error);
        Assert.Equal(1, store.LastSeenContentVersion);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.True(Directory.Exists(StorePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [WindowsReadOnlyNewsFact]
    public void ReadableButReadOnlyProgressSuppressesAutomaticNewsAcrossRestarts()
    {
        Store().RecordSeen(1);
        var original = File.GetAttributes(StorePath);
        File.SetAttributes(StorePath, original | FileAttributes.ReadOnly);
        try
        {
            for (var launch = 0; launch < 2; launch++)
            {
                var session = Session(version: 2);
                Assert.Null(session.Error);
                Assert.False(session.TryShow(false, UnexpectedShow));
                Assert.NotNull(session.Error);
                Assert.False(session.TryShow(false, UnexpectedShow));
                Assert.True(session.TryShowManually(false, () => { }));
                Assert.Equal(1, Store().LastSeenContentVersion);
                Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
            }
        }
        finally { File.SetAttributes(StorePath, original); }

        Assert.True(Session(version: 2).TryShow(false, () => { }));
        Assert.False(Session(version: 2).TryShow(false, UnexpectedShow));
    }

    [Fact]
    public void NewsProgressNeverChangesSetupOrDictationSettings()
    {
        Directory.CreateDirectory(_directory);
        var setup = new SetupPreferencesStore(Path.Combine(_directory, "setup.json"));
        setup.Save(4, true);
        var audio = Path.Combine(_directory, "audio.json");
        File.WriteAllText(audio, "existing audio preferences");
        Assert.True(Session().TryShow(false, () => { }));
        Assert.True(new SetupPreferencesStore(Path.Combine(_directory, "setup.json")).Current.Completed);
        Assert.Equal("existing audio preferences", File.ReadAllText(audio));
    }

    [Fact]
    public void OpeningAWindowCancelsTheNewsOfAnOrdinaryLaunch()
    {
        var session = Session();
        Assert.True(session.SuppressForNavigation());
        Assert.False(session.Pending);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void AfterAnUpdateOpeningSettingsKeepsTheNewsButWorkStillCancelsIt()
    {
        var session = Session(afterUpdate: true);
        Assert.True(session.AfterUpdate);
        Assert.False(session.SuppressForNavigation());
        Assert.True(session.Pending);
        var shows = 0;
        Assert.True(session.TryShow(false, () => shows++));
        Assert.Equal(1, shows);
        Assert.Equal(1, Store().LastSeenContentVersion);

        var busy = Session(version: 2, afterUpdate: true);
        Assert.False(busy.TryShow(true, UnexpectedShow));
        var dictating = Session(version: 2, afterUpdate: true);
        dictating.Suppress();
        Assert.False(dictating.TryShow(false, UnexpectedShow));
        Assert.Equal(1, Store().LastSeenContentVersion);
    }

    [Fact]
    public void AnUpdateRestartDoesNotRepeatSeenNewsOrInterruptAnUnfinishedSetup()
    {
        Assert.True(Session().TryShow(false, () => { }));
        Assert.False(Session(afterUpdate: true).Pending);
        Directory.Delete(_directory, recursive: true);
        Assert.False(Session(completed: false, afterUpdate: true).TryShow(false, UnexpectedShow));
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--settings" }, true)]
    [InlineData(new[] { "--page", "about" }, true)]
    [InlineData(new[] { "--dictionary" }, true)]
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--transcribe-file", @"C:\audio.wav" }, false)]
    [InlineData(new[] { "--unknown" }, false)]
    public void OnlyWindowAndPageRequestsCountAsNavigation(string[] arguments, bool navigates) =>
        Assert.Equal(navigates, ApplicationActivationRequest.Parse(arguments).OnlyNavigates);

    private static void UnexpectedShow() => Assert.Fail("News must not interrupt this launch.");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

public sealed class WindowsReadOnlyNewsFactAttribute : FactAttribute
{
    public WindowsReadOnlyNewsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows read-only file replacement semantics.";
    }
}
