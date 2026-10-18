using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ManagedPolicyServiceTests
{
    private sealed class FakeSource : IManagedPolicySource
    {
        private int _reads;
        public Func<ManagedPolicyReading> Next { get; set; } = () => ManagedPolicyReading.Absent;
        public int Reads => Volatile.Read(ref _reads);

        public ManagedPolicyReading Read()
        {
            Interlocked.Increment(ref _reads);
            return Next();
        }

        public event Action? Changed;
        public void RaiseChanged() => Changed?.Invoke();
    }

    private static ManagedPolicyReading Enforcing(string key, long value) => new(true, null,
        ManagedPolicyRawSection.Of([KeyValuePair.Create(key, ManagedPolicyRawValue.FromNumber(value))]),
        ManagedPolicyRawSection.Missing);

    private static ManagedPolicyService Create(FakeSource source, TimeSpan? fallback = null, TimeSpan? debounce = null,
        Action<string, Exception?>? report = null) =>
        new(source, fallback ?? TimeSpan.Zero, debounce ?? TimeSpan.FromMilliseconds(10), report);

    // Subscribes before the trigger runs so a change published on another thread is never missed.
    private static async Task<ManagedPolicySnapshot> NextChangeAsync(ManagedPolicyService service, Action trigger)
    {
        var published = new TaskCompletionSource<ManagedPolicySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ManagedPolicySnapshot snapshot) => published.TrySetResult(snapshot);
        service.Changed += Handler;
        try
        {
            trigger();
            return await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            service.Changed -= Handler;
        }
    }

    [Fact]
    public void Start_PublishesTheFirstReadEvenWhenPolicyIsAbsent()
    {
        var source = new FakeSource();
        using var service = Create(source);
        var published = new List<ManagedPolicySnapshot>();
        service.Changed += published.Add;

        var snapshot = service.Start();

        Assert.Equal(1, snapshot.Revision);
        Assert.Equal(ManagedPolicyReadStatus.Absent, snapshot.Status);
        Assert.NotEqual(DateTimeOffset.MinValue, snapshot.ReadAt);
        Assert.Same(snapshot, service.Current);
        Assert.Same(snapshot, Assert.Single(published));
        Assert.Equal(1, source.Reads);

        Assert.Same(snapshot, service.Start());
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void Refresh_WithoutContentChange_KeepsTheSnapshot()
    {
        var source = new FakeSource();
        using var service = Create(source);
        var raised = 0;
        service.Changed += _ => raised++;
        var before = service.Start();

        var after = service.Refresh();

        Assert.Same(before, after);
        Assert.Equal(2, source.Reads);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Refresh_WithContentChange_PublishesANewRevision()
    {
        var source = new FakeSource();
        using var service = Create(source);
        var published = new List<ManagedPolicySnapshot>();
        service.Changed += published.Add;
        service.Start();

        source.Next = () => Enforcing(ManagedPolicyKeys.AllowCloudTranscription, 0);
        var snapshot = service.Refresh();

        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(ManagedPolicyReadStatus.Present, snapshot.Status);
        Assert.False(snapshot.Enforced(ManagedPolicyKeys.AllowCloudTranscription)!.IsTrue);
        Assert.Equal(2, published.Count);
        Assert.Same(snapshot, service.Current);
    }

    [Fact]
    public void UnreadableRead_RetainsValuesAndBlocksUntilReadableAgain()
    {
        var source = new FakeSource { Next = () => Enforcing(ManagedPolicyKeys.AllowCloudTranscription, 0) };
        using var service = Create(source);
        service.Start();

        source.Next = () => ManagedPolicyReading.Unreadable("IOException");
        var unreadable = service.Refresh();

        Assert.Equal(ManagedPolicyReadStatus.Unreadable, unreadable.Status);
        Assert.Equal(2, unreadable.Revision);
        Assert.False(unreadable.Enforced(ManagedPolicyKeys.AllowCloudTranscription)!.IsTrue);
        Assert.True(unreadable.IsBlocked(ManagedPolicyKeys.AllowCloudTranscription));
        Assert.True(unreadable.IsBlocked(ManagedPolicyKeys.LaunchAtLogin));
        Assert.Contains("IOException", Assert.Single(unreadable.Issues).Message);
        Assert.Same(unreadable, service.Refresh());

        source.Next = () => Enforcing(ManagedPolicyKeys.AllowCloudTranscription, 0);
        var readable = service.Refresh();

        Assert.Equal(ManagedPolicyReadStatus.Present, readable.Status);
        Assert.Equal(3, readable.Revision);
        Assert.False(readable.Enforced(ManagedPolicyKeys.AllowCloudTranscription)!.IsTrue);
        Assert.False(readable.IsBlocked(ManagedPolicyKeys.AllowCloudTranscription));
        Assert.Empty(readable.Issues);
    }

    [Fact]
    public void ColdStartWithUnreadableSource_BlocksWithoutValues()
    {
        var source = new FakeSource { Next = () => ManagedPolicyReading.Unreadable("SecurityException") };
        using var service = Create(source);

        var snapshot = service.Start();

        Assert.Equal(ManagedPolicyReadStatus.Unreadable, snapshot.Status);
        Assert.Empty(snapshot.EnforcedValues);
        Assert.True(snapshot.IsBlocked(ManagedPolicyKeys.AllowPluginDownloads));
    }

    [Fact]
    public void ReadException_IsReportedAndTreatedAsUnreadable()
    {
        var reported = new List<(string Stage, Exception? Error)>();
        var source = new FakeSource { Next = () => throw new IOException("locked") };
        using var service = Create(source, report: (stage, error) => reported.Add((stage, error)));

        var snapshot = service.Start();

        Assert.Equal(ManagedPolicyReadStatus.Unreadable, snapshot.Status);
        Assert.Contains("IOException", Assert.Single(snapshot.Issues).Message);
        var failure = Assert.Single(reported);
        Assert.Equal("policy.read-failed", failure.Stage);
        Assert.IsType<IOException>(failure.Error);
    }

    [Fact]
    public async Task SourceChange_RefreshesOnceAfterTheDebounce()
    {
        var source = new FakeSource();
        using var service = Create(source, debounce: TimeSpan.FromMilliseconds(50));
        service.Start();

        var snapshot = await NextChangeAsync(service, () =>
        {
            source.Next = () => Enforcing(ManagedPolicyKeys.LaunchAtLogin, 1);
            source.RaiseChanged();
            source.RaiseChanged();
            source.RaiseChanged();
        });

        Assert.True(snapshot.Enforced(ManagedPolicyKeys.LaunchAtLogin)!.IsTrue);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task FallbackInterval_ReadsWithoutNotifications()
    {
        var source = new FakeSource();
        using var service = Create(source, fallback: TimeSpan.FromMilliseconds(20));
        service.Start();

        var snapshot = await NextChangeAsync(service, () => source.Next = () => Enforcing(ManagedPolicyKeys.LaunchAtLogin, 1));

        Assert.True(snapshot.Enforced(ManagedPolicyKeys.LaunchAtLogin)!.IsTrue);
        Assert.True(source.Reads >= 2);
    }

    [Fact]
    public async Task Dispose_StopsNotificationsAndTimers()
    {
        var source = new FakeSource();
        var service = Create(source, fallback: TimeSpan.FromMilliseconds(20));
        var current = service.Start();

        service.Dispose();
        var reads = source.Reads;
        source.Next = () => Enforcing(ManagedPolicyKeys.LaunchAtLogin, 1);
        source.RaiseChanged();
        await Task.Delay(150);

        Assert.Equal(reads, source.Reads);
        Assert.Same(current, service.Refresh());
        Assert.Same(current, service.Current);
    }

    [Fact]
    public void ChangedHandlerFailure_IsReportedAndDoesNotPreventPublishing()
    {
        var reported = new List<string>();
        var source = new FakeSource();
        using var service = Create(source, report: (stage, _) => reported.Add(stage));
        service.Changed += _ => throw new InvalidOperationException("handler");

        var snapshot = service.Start();

        Assert.Equal(1, snapshot.Revision);
        Assert.Same(snapshot, service.Current);
        Assert.Equal(["policy.changed-handler-failed"], reported);
    }
}
