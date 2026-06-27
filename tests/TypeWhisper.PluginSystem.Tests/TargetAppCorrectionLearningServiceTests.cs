using System.IO;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class TargetAppCorrectionLearningServiceTests : IDisposable
{
    private readonly string _dictionaryPath = Path.GetTempFileName();
    private readonly DictionaryService _dictionary;

    public TargetAppCorrectionLearningServiceTests()
    {
        _dictionary = new DictionaryService(_dictionaryPath);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsHighConfidenceCorrectionOnCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        Assert.Contains(_dictionary.Entries, entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            entry.Original == "teh" &&
            entry.Replacement == "the");
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotQueryFocusedElementWhenCommitSignalRecaptures()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(0, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhileFocusRemainsSameWithoutCommit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([false, false]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Equal(2, observer.RecaptureCalls);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhenSameElementEditStabilizesWithoutCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            new FakeCommitObserver([false, false, false, false]),
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)
            ],
            (_, _) => Task.CompletedTask);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
        Assert.Equal(4, observer.RecaptureCalls);
        Assert.Equal(4, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnRepeatedSameElementEditWithoutCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            new FakeCommitObserver([false, false, false]),
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)],
            (_, _) => Task.CompletedTask);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
        Assert.Equal(3, observer.RecaptureCalls);
        Assert.Equal(3, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsWhenSameElementEditIsCommittedByNewLine()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world\r\n"));
        var service = CreateService(observer, new FakeCommitObserver([false]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        Assert.Equal(1, observer.RecaptureCalls);
        Assert.Equal(1, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotTreatExistingLineBreakAsSameElementCommit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world\r\nnext");
        observer.Recaptures.Enqueue(Observation("hello the world\r\nnext"));
        var service = CreateService(observer, new FakeCommitObserver([false]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
        Assert.Equal(1, observer.RecaptureCalls);
        Assert.Equal(1, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhenSameElementEditKeepsChanging()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation("hello then world"));
        var service = CreateService(observer, new FakeCommitObserver([false, false]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
        Assert.Equal(2, observer.RecaptureCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_TimesOutWithoutCommit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([false]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Equal(1, observer.RecaptureCalls);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsOnFocusChange()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Matches.Enqueue(TargetAppTextElementMatch.Same);
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        var service = CreateService(observer, new FakeCommitObserver([false, false]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        Assert.Equal(2, observer.RecaptureCalls);
        Assert.Equal(2, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhenFocusChangeRecaptureIsUnsupported()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(null);
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        var service = CreateService(observer, new FakeCommitObserver([false]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Equal(1, observer.RecaptureCalls);
        Assert.Equal(1, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhenCommittedAfterFocusMovedToDifferentElement()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(new TargetAppTextObservation("other", "hello the world", new IntPtr(42)));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnWhenEditIsUndoneBeforeCommit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(baseline);
        var service = CreateService(observer, new FakeCommitObserver([false, false, true]), polls: 3);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsWhenInsertedTextAppearsEarlierInDocument()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world\r\nhello teh world");
        observer.Recaptures.Enqueue(Observation("hello teh world\r\nhello the world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("hello teh world", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsWhenAutomationCommitSignalArrives()
    {
        var observer = new FakeTargetTextObserver();
        var commitObserver = new FakeCommitObserver([]);
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            commitObserver,
            [TimeSpan.Zero],
            (_, _) =>
            {
                serviceSignal(commitObserver);
                return Task.CompletedTask;
            });

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);

        static void serviceSignal(FakeCommitObserver observer)
        {
            observer.SignalCommitForAutomation();
        }
    }

    [Fact]
    public async Task TrackInsertionAsync_IgnoresAutomationCommitSignalRaisedBeforeTrackingStarts()
    {
        var observer = new FakeTargetTextObserver();
        var commitObserver = new FakeCommitObserver([]);
        var baseline = Observation("hello watre world");
        var delayCalls = 0;
        commitObserver.SignalCommitForAutomation();
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            commitObserver,
            [TimeSpan.Zero, TimeSpan.Zero],
            (_, _) =>
            {
                if (Interlocked.Increment(ref delayCalls) == 2)
                {
                    observer.Recaptures.Enqueue(Observation("hello water world"));
                    commitObserver.SignalCommitForAutomation();
                }

                return Task.CompletedTask;
            });

        var learned = await service.TrackInsertionAsync("watre", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("watre", correction.Original);
        Assert.Equal("water", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLetCancelledPriorRunClearNextCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var commitObserver = new FakeCommitObserver([], clearSignalOnStop: true);
        var baseline = Observation("hello watre world");
        observer.Recaptures.Enqueue(Observation("hello water world"));
        var firstDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDelayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDelayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCalls = 0;
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            commitObserver,
            [TimeSpan.Zero],
            async (_, token) =>
            {
                var call = Interlocked.Increment(ref delayCalls);
                if (call == 1)
                {
                    firstDelayEntered.SetResult();
                    await firstDelayRelease.Task;
                    token.ThrowIfCancellationRequested();
                    return;
                }

                secondDelayEntered.SetResult();
                await secondDelayRelease.Task;
            });
        using var firstCts = new CancellationTokenSource();

        var firstTask = service.TrackInsertionAsync("teh", Observation("hello teh world"), firstCts.Token);
        await firstDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        firstCts.Cancel();

        var secondTask = service.TrackInsertionAsync("hello watre world", baseline);
        var secondStartedBeforeFirstStopped = await WaitForSignalAsync(secondDelayEntered.Task, TimeSpan.FromMilliseconds(100));
        if (secondStartedBeforeFirstStopped)
            commitObserver.SignalCommitForAutomation();

        firstDelayRelease.SetResult();
        await firstTask;

        if (!secondStartedBeforeFirstStopped)
        {
            await secondDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            commitObserver.SignalCommitForAutomation();
        }

        secondDelayRelease.SetResult();
        var learned = await secondTask;

        var correction = Assert.Single(learned);
        Assert.Equal("watre", correction.Original);
        Assert.Equal("water", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_DefaultScheduleTimesOutAfterThirtySeconds()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        var delays = new List<TimeSpan>();
        var service = new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            new FakeCommitObserver([]),
            pollSchedule: null,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Equal(30, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay));
        Assert.Equal(30, observer.RecaptureCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_SkipsDuplicateExistingCorrection()
    {
        _dictionary.LearnCorrection("teh", "THE");
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        var correction = Assert.Single(_dictionary.Entries);
        Assert.Equal("THE", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_ReturnsEmptyForUnsupportedCapture()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(null);
        observer.Matches.Enqueue(TargetAppTextElementMatch.Unavailable);
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
    }

    private TargetAppCorrectionLearningService CreateService(
        FakeTargetTextObserver observer,
        FakeCommitObserver commitObserver,
        int polls = 1)
    {
        return new TargetAppCorrectionLearningService(
            _dictionary,
            observer,
            commitObserver,
            Enumerable.Repeat(TimeSpan.Zero, polls).ToArray(),
            (_, _) => Task.CompletedTask);
    }

    private static TargetAppTextObservation Observation(string value)
        => new("editable", value, new IntPtr(42));

    private static async Task<bool> WaitForSignalAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        return completed == task;
    }

    public void Dispose()
    {
        if (File.Exists(_dictionaryPath))
            File.Delete(_dictionaryPath);
    }

    private sealed class FakeTargetTextObserver : ITargetAppTextObserver
    {
        public Queue<TargetAppTextObservation?> Recaptures { get; } = new();

        public Queue<TargetAppTextElementMatch> Matches { get; } = new();

        public int FocusedElementMatchCalls { get; private set; }

        public int RecaptureCalls { get; private set; }

        public TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength)
            => Observation("unused");

        public TargetAppTextObservation? Recapture(TargetAppTextObservation baseline)
        {
            RecaptureCalls++;
            return Recaptures.Count > 0 ? Recaptures.Dequeue() : baseline;
        }

        public TargetAppTextElementMatch GetFocusedElementMatch(TargetAppTextObservation baseline)
        {
            FocusedElementMatchCalls++;
            return Matches.Count > 0 ? Matches.Dequeue() : TargetAppTextElementMatch.Same;
        }
    }

    private sealed class FakeCommitObserver : ITargetAppCorrectionCommitObserver
    {
        private readonly Queue<bool> _signals;
        private readonly bool _clearSignalOnStop;
        private int _automationSignal;

        public FakeCommitObserver(IEnumerable<bool> signals, bool clearSignalOnStop = false)
        {
            _signals = new Queue<bool>(signals);
            _clearSignalOnStop = clearSignalOnStop;
        }

        public void Start()
        {
            Interlocked.Exchange(ref _automationSignal, 0);
        }

        public void Stop()
        {
            if (_clearSignalOnStop)
                Interlocked.Exchange(ref _automationSignal, 0);
        }

        public bool ConsumeCommitSignal()
        {
            if (Interlocked.Exchange(ref _automationSignal, 0) == 1)
                return true;

            return _signals.Count > 0 && _signals.Dequeue();
        }

        public void SignalCommitForAutomation()
        {
            Interlocked.Exchange(ref _automationSignal, 1);
        }

        public void Dispose()
        {
        }
    }
}
