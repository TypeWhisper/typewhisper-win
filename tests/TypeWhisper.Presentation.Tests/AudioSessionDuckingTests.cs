using TypeWhisper.WinUI;
using Xunit;

public sealed class AudioSessionDuckingTests : IDisposable
{
    private const uint Own = 100;
    private const string Device = "speakers";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-ducking-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly List<IReadOnlyList<AudioDuckingJournalEntry>> _journal = [];

    private IReadOnlyList<AudioDuckingJournalEntry> Journal => _journal.Count == 0 ? [] : _journal[^1];

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private AudioSessionDucking Create() => new(Own, entries => _journal.Add(entries), () => _now, (_, ex) => throw ex);

    [Fact]
    public void LowersOtherAppsButNotTypeWhisperAndRestoresThem()
    {
        var music = new FakeSession("music", 1, .8f);
        var own = new FakeSession("typewhisper", Own, 1f);
        var ducking = Create();

        ducking.Duck(Device, .1f, [music, own]);

        Assert.Equal(.08f, music.Volume, 3);
        Assert.Equal(1f, own.Volume);
        ducking.Restore();
        Assert.Equal(.8f, music.Volume);
        Assert.Empty(Journal);
    }

    [Fact]
    public void JournalsEachVolumeBeforeLoweringIt()
    {
        var music = new FakeSession("music", 1, .8f);
        var ducking = Create();
        IReadOnlyList<AudioDuckingJournalEntry>? written = null;
        music.Changing = _ => written = Journal;

        ducking.Duck(Device, .5f, [music]);

        var entry = Assert.Single(written!);
        Assert.Equal(new AudioDuckingJournalEntry(Device, "app:music", .8f, .4f, _now), entry);
    }

    [Fact]
    public void VolumeChangedDuringRecordingIsKept()
    {
        var music = new FakeSession("music", 1, .8f);
        var ducking = Create();
        ducking.Duck(Device, .1f, [music]);

        music.Volume = .5f;
        ducking.Restore();

        Assert.Equal(.5f, music.Volume);
    }

    [Fact]
    public void AppThatStartsDuringRecordingIsLoweredAndRestored()
    {
        var ducking = Create();
        ducking.Duck(Device, .2f, []);
        var video = new FakeSession("video", 2, 1f);

        ducking.Add(video);
        ducking.Add(video);
        Assert.Equal(.2f, video.Volume, 3);
        ducking.Restore();
        Assert.Equal(1f, video.Volume);

        var later = new FakeSession("later", 3, 1f);
        ducking.Add(later);
        Assert.Equal(1f, later.Volume);
    }

    [Fact]
    public void SilentAndUnchangedSessionsAreNotJournaled()
    {
        var ducking = Create();
        ducking.Duck(Device, 1f, [new FakeSession("music", 1, .8f)]);
        ducking.Restore();
        ducking.Duck(Device, .1f, [new FakeSession("muted", 2, 0f)]);
        Assert.Empty(Journal);
    }

    [Fact]
    public void InterruptedRunIsRestoredWhenTheAppPlaysAgain()
    {
        var previous = new[]
        {
            new AudioDuckingJournalEntry(Device, "app:music", .8f, .08f, _now),
            new AudioDuckingJournalEntry(Device, "app:changed", .8f, .08f, _now),
            new AudioDuckingJournalEntry(Device, "app:closed", .8f, .08f, _now),
            new AudioDuckingJournalEntry("headset", "app:music", .6f, .06f, _now),
        };
        var music = new FakeSession("music", 7, .08f);
        var changed = new FakeSession("changed", 8, .3f);
        var ducking = Create();
        ducking.Resume(previous);

        ducking.Recover(Device, [music, changed]);

        Assert.Equal(.8f, music.Volume);
        Assert.Equal(.3f, changed.Volume);
        Assert.Equal([previous[2], previous[3]], Journal);
        Assert.Equal([Device, "headset"], ducking.PendingDevices);
    }

    [Fact]
    public void DuckingStartsFromTheVolumeAnInterruptedRunSaved()
    {
        var ducking = Create();
        ducking.Resume([new AudioDuckingJournalEntry(Device, "app:music", .8f, .08f, _now)]);
        var music = new FakeSession("music", 7, .08f);

        ducking.Duck(Device, .5f, [music]);
        Assert.Equal(.4f, music.Volume, 3);
        ducking.Restore();

        Assert.Equal(.8f, music.Volume);
        Assert.Empty(Journal);
    }

    [Fact]
    public void OldEntriesAreDropped()
    {
        var ducking = Create();
        ducking.Resume([new AudioDuckingJournalEntry(Device, "app:music", .8f, .08f, _now - AudioSessionDucking.PendingLifetime - TimeSpan.FromMinutes(1))]);
        Assert.Empty(ducking.PendingDevices);
    }

    [Fact]
    public void FailedRestoreStaysPendingForTheNextStart()
    {
        var music = new FakeSession("music", 1, .8f);
        var ducking = new AudioSessionDucking(Own, entries => _journal.Add(entries), () => _now, (_, _) => { });
        ducking.Duck(Device, .1f, [music]);

        music.Changing = _ => throw new InvalidOperationException("Session is gone.");
        ducking.Restore();

        var entry = Assert.Single(Journal);
        Assert.Equal("app:music", entry.SessionId);
        Assert.False(ducking.IsDucked);
    }

    [Fact]
    public void JournalRoundTripsAndDisappearsWhenEmpty()
    {
        var path = Path.Combine(_directory, "audio-ducking.json");
        Assert.Empty(AudioDuckingJournal.Load(path));
        var entries = new[] { new AudioDuckingJournalEntry(Device, "app:music", .8f, .08f, _now) };

        AudioDuckingJournal.Save(path, entries);
        Assert.Equal(entries, AudioDuckingJournal.Load(path));
        AudioDuckingJournal.Save(path, []);

        Assert.False(File.Exists(path));
    }

    private sealed class FakeSession(string app, uint processId, float volume) : IDuckableAudioSession
    {
        private float _volume = volume;
        public Action<float>? Changing { get; set; }
        public string SessionId { get; } = "app:" + app;
        public string InstanceId { get; } = $"app:{app}|{processId}";
        public uint ProcessId { get; } = processId;
        public float Volume
        {
            get => _volume;
            set { Changing?.Invoke(value); _volume = value; }
        }
    }
}
