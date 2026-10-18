using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WhatsNewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "whats-new-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_directory, "whats-new.json");
    private WhatsNewPreferencesStore Store() => new(StorePath);
    private WhatsNewSession Session(bool completed = true, int version = 1, ApplicationActivationRequest? request = null) =>
        new(Store(), request ?? ApplicationActivationRequest.Parse([]), completed, version);

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
    public void FailedWindowCreationDoesNotConsumeThePersistedVersionOrRetryDuringTheSession()
    {
        var session = Session();
        Assert.Throws<InvalidOperationException>(() => session.TryShow(false, () => throw new InvalidOperationException("Window unavailable")));
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.False(File.Exists(StorePath));
        Assert.True(Session().TryShow(false, () => { }));
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
    public void FailedSaveKeepsPreviousVersionAndDoesNotBlockOrRepeatTheView()
    {
        var store = Store();
        store.RecordSeen(1);
        var session = new WhatsNewSession(store, ApplicationActivationRequest.Parse([]), true, 2);
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        Assert.True(session.TryShow(false, () => { }));
        Assert.NotNull(session.Error);
        Assert.Equal(1, store.LastSeenContentVersion);
        Assert.False(session.TryShow(false, UnexpectedShow));
        Assert.True(Directory.Exists(StorePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
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

    private static void UnexpectedShow() => Assert.Fail("News must not interrupt this launch.");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
