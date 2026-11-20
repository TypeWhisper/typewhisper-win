using System.IO;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class TargetAppCorrectionLearningServiceTests : IDisposable
{
    private readonly string _dictionaryPath = Path.GetTempFileName();
    private readonly DictionaryService _dictionary;

    public TargetAppCorrectionLearningServiceTests()
    {
        _dictionary = new DictionaryService(_dictionaryPath);
    }

    [Fact]
    public async Task WholeSentenceRewriteDoesNotCreateWordMappings()
    {
        var observer = new FakeTargetTextObserver();
        observer.Recaptures.Enqueue(Observation("Please bring my red book"));
        var service = CreateService(observer, new FakeCommitObserver([true]));
        var result = await service.TrackInsertionAsync("We use this old tool", Observation("We use this old tool"));
        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.AmbiguousEdit, result.Outcome);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsHighConfidenceCorrectionOnCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.Learned, learned.Outcome);
        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        Assert.Contains(_dictionary.Entries, entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            entry.Original == "teh" &&
            entry.Replacement == "the");
    }

    [Fact]
    public async Task TrackInsertionAsync_ReportsNoEditOnCommitSignal()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(baseline);
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var result = await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.NoEdit, result.Outcome);
        Assert.Empty(result.LearnedCorrections);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    // Edge reads an emptied text area as its label.
    [InlineData("Message")]
    [InlineData("Type a message")]
    public async Task TrackInsertionAsync_LearnsFromTheLastReadWhenEnterSendsAndEmptiesTheField(string afterSend)
    {
        // The poll sees the edit; Enter then sends the chat message and the field is empty when the commit is read.
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the world"));
        observer.Recaptures.Enqueue(Observation(afterSend) with { Labels = ["Message", "Type a message"] });
        var service = CreateService(observer, new FakeCommitObserver([false, true]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_KeepsTheVerdictOfAFieldReplacedWithOtherText()
    {
        // The user replaces the observed edit with unrelated text and commits: that is an edit, not a placeholder.
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello ten world"));
        observer.Recaptures.Enqueue(Observation("Never mind") with { Labels = ["Message", ""] });
        var service = CreateService(observer, new FakeCommitObserver([false, true]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_NeverLearnsFromTheLabelOfAnEmptiedField()
    {
        // Sent without an edit; the emptied field reads as a label that would look like a correction.
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(baseline);
        observer.Recaptures.Enqueue(Observation("hello the world") with { Labels = ["hello the world"] });
        var service = CreateService(observer, new FakeCommitObserver([false, true]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.NoEdit, learned.Outcome);
        Assert.Empty(_dictionary.Entries);
    }

    [Theory]
    [InlineData("hello the world", "Message", true)]
    // Sent without an edit: the label that looks like a correction must not become the last read.
    [InlineData("hello teh world", "hello the world", false)]
    public async Task TrackInsertionAsync_DoesNotKeepAnEmptiedFieldAsTheLastRead(string edited, string label, bool learns)
    {
        // Enter lands between a poll's commit check and its read, so that poll reads the emptied field.
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation(edited));
        observer.Recaptures.Enqueue(Observation(label) with { Labels = [label] });
        observer.Recaptures.Enqueue(Observation(label) with { Labels = [label] });
        var service = CreateService(observer, new FakeCommitObserver([false, false, true]), polls: 3);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(learns, learned.Count == 1);
        Assert.Equal(learns ? 1 : 0, _dictionary.Entries.Count);
    }

    [Theory]
    [InlineData("", null, true)]
    [InlineData(" \r\n", null, true)]
    [InlineData("Message", "Message", true)]
    [InlineData(" Message ", "Message", true)]
    [InlineData("Never mind", "Message", false)]
    [InlineData("Message", null, false)]
    [InlineData("message", "Message", false)]
    public void WasEmptied_NeedsABlankFieldOrItsOwnLabel(string value, string? label, bool emptied)
    {
        var observation = Observation(value) with { Labels = label is null ? null : [label, ""] };

        Assert.Equal(emptied, TargetAppCorrectionLearningService.WasEmptied(observation));
    }

    [Fact]
    public async Task TrackInsertionAsync_KeepsTheVerdictOfAFieldThatStillHoldsText()
    {
        // A poll sees one edit, the user edits on, and the committed text is ambiguous: nothing is learned from the poll.
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello ten world"));
        observer.Recaptures.Enqueue(Observation("hello the new world"));
        var service = CreateService(observer, new FakeCommitObserver([false, true]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
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
            _dictionary.LearnCorrections,
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
            _dictionary.LearnCorrections,
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

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.NoCommitBeforeTimeout, learned.Outcome);
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
    public async Task TrackInsertionAsync_DoesNotStopEarlyWhenElectronFocusReportsDocumentBeforeEdit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = new TargetAppTextObservation(
            "compose",
            "hello teh world",
            new IntPtr(42),
            AllowElementKeyChangeOnCommit: true);
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello teh world", new IntPtr(42)));
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello the world", new IntPtr(42)));
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello the world", new IntPtr(42)));
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        var service = CreateService(observer, new FakeCommitObserver([false, false, true]), polls: 3);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        Assert.Equal(3, observer.RecaptureCalls);
        Assert.Equal(2, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotLearnElectronSameWindowEditBeforeCommit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = new TargetAppTextObservation(
            "compose",
            "hello teh world",
            new IntPtr(42),
            AllowElementKeyChangeOnCommit: true);
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello the world", new IntPtr(42)));
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello the world", new IntPtr(42)));
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        observer.Matches.Enqueue(TargetAppTextElementMatch.Different);
        var service = CreateService(observer, new FakeCommitObserver([false, false]), polls: 2);

        var learned = await service.TrackInsertionAsync("teh", baseline);

        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
        Assert.Equal(2, observer.RecaptureCalls);
        Assert.Equal(2, observer.FocusedElementMatchCalls);
    }

    [Fact]
    public async Task TrackInsertionAsync_LearnsElectronEditAfterWindowFocusChange()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = new TargetAppTextObservation(
            "compose",
            "hello teh world",
            new IntPtr(42),
            AllowElementKeyChangeOnCommit: true);
        observer.Recaptures.Enqueue(new TargetAppTextObservation("document", "hello the world", new IntPtr(42)));
        observer.Matches.Enqueue(TargetAppTextElementMatch.DifferentWindow);
        var service = CreateService(observer, new FakeCommitObserver([false]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
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
    public async Task TrackInsertionAsync_LearnsCommittedSameWindowRecaptureWhenBaselineAllowsElementKeyChange()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = new TargetAppTextObservation(
            "editable",
            "hello teh world",
            new IntPtr(42),
            AllowElementKeyChangeOnCommit: true);
        observer.Recaptures.Enqueue(new TargetAppTextObservation("other", "hello the world", new IntPtr(42)));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var learned = await service.TrackInsertionAsync("teh", baseline);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
    }

    [Fact]
    public async Task TrackInsertionAsync_DoesNotUseElementKeyChangeFallbackAcrossWindows()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = new TargetAppTextObservation(
            "editable",
            "hello teh world",
            new IntPtr(42),
            AllowElementKeyChangeOnCommit: true);
        observer.Recaptures.Enqueue(new TargetAppTextObservation("other", "hello the world", new IntPtr(99)));
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
            _dictionary.LearnCorrections,
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
            _dictionary.LearnCorrections,
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
            _dictionary.LearnCorrections,
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
            _dictionary.LearnCorrections,
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
    public async Task TrackInsertionAsync_ASlowTargetAppIsReadLessOftenAndOnlyForThirtySeconds()
    {
        // Each poll makes the target app answer for two seconds.
        var clock = new ManualClock();
        var observer = new FakeTargetTextObserver { OnPoll = () => clock.Advance(TimeSpan.FromSeconds(2)) };
        var delays = new List<TimeSpan>();
        var service = new TargetAppCorrectionLearningService(
            _dictionary.LearnCorrections,
            observer,
            new FakeCommitObserver([]),
            pollSchedule: null,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                clock.Advance(delay);
                return Task.CompletedTask;
            },
            time: clock);

        var learned = await service.TrackInsertionAsync("teh", Observation("hello teh world"));

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.NoCommitBeforeTimeout, learned.Outcome);
        Assert.All(delays.Skip(1), delay => Assert.True(delay >= TimeSpan.FromSeconds(2)));
        Assert.True(observer.FocusedElementMatchCalls <= 8, $"{observer.FocusedElementMatchCalls} polls");
        // No read starts after 30 seconds, and the last one ends within one poll of it.
        Assert.True(clock.Elapsed <= TimeSpan.FromSeconds(32), $"{clock.Elapsed} observed");
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

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.DuplicateCorrection, learned.Outcome);
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

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.UnsupportedTextObservation, learned.Outcome);
        Assert.Empty(learned);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task TrackInsertionAsync_ReportsAmbiguousEdit()
    {
        var observer = new FakeTargetTextObserver();
        var baseline = Observation("hello teh world");
        observer.Recaptures.Enqueue(Observation("hello the better world"));
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var result = await service.TrackInsertionAsync("teh", baseline);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.AmbiguousEdit, result.Outcome);
        Assert.Empty(result);
    }

    [Fact]
    public async Task TrackInsertionAsync_ReportsCancelledAttempt()
    {
        var service = CreateService(new FakeTargetTextObserver(), new FakeCommitObserver([]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.TrackInsertionAsync("teh", Observation("hello teh world"), cts.Token);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task TrackInsertionAsync_ReportsExpectedObservationFailure()
    {
        var observer = new FakeTargetTextObserver { RecaptureException = new InvalidOperationException() };
        var service = CreateService(observer, new FakeCommitObserver([true]));

        var result = await service.TrackInsertionAsync("teh", Observation("hello teh world"));

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.Failed, result.Outcome);
    }

    [Fact]
    public void CompleteAttempt_IgnoresStaleAttempt()
    {
        var service = CreateService(new FakeTargetTextObserver(), new FakeCommitObserver([]));
        var staleAttempt = service.BeginAttempt();
        var currentAttempt = service.BeginAttempt();

        service.CompleteAttempt(currentAttempt, TargetAppCorrectionLearningOutcomeKind.Learned);
        service.CompleteAttempt(staleAttempt, TargetAppCorrectionLearningOutcomeKind.Cancelled);

        Assert.Equal(TargetAppCorrectionLearningOutcomeKind.Learned, service.LastOutcome?.Outcome);
    }

    [Theory]
    [InlineData("Learned", "learned")]
    [InlineData("UnsupportedTextObservation", "unsupported_text_observation")]
    [InlineData("NoEdit", "no_edit")]
    [InlineData("AmbiguousEdit", "ambiguous_edit")]
    [InlineData("NoCommitBeforeTimeout", "no_commit_before_timeout")]
    [InlineData("DuplicateCorrection", "duplicate_correction")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("Failed", "failed")]
    public void Outcome_UsesStableDiagnosticCode(
        string outcomeName,
        string expectedCode)
    {
        var outcome = Enum.Parse<TargetAppCorrectionLearningOutcomeKind>(outcomeName);
        var status = new TargetAppCorrectionLearningOutcome(outcome, DateTimeOffset.UnixEpoch);

        Assert.Equal(expectedCode, status.Code);
    }

    private TargetAppCorrectionLearningService CreateService(
        FakeTargetTextObserver observer,
        FakeCommitObserver commitObserver,
        int polls = 1)
    {
        return new TargetAppCorrectionLearningService(
            _dictionary.LearnCorrections,
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

        public Exception? RecaptureException { get; init; }

        public TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength)
            => Observation("unused");

        public TargetAppTextObservation? Recapture(TargetAppTextObservation baseline)
        {
            RecaptureCalls++;
            if (RecaptureException is not null)
                throw RecaptureException;

            return Recaptures.Count > 0 ? Recaptures.Dequeue() : baseline;
        }

        public Action? OnPoll { get; init; }

        public TargetAppTextElementMatch GetFocusedElementMatch(TargetAppTextObservation baseline)
        {
            FocusedElementMatchCalls++;
            OnPoll?.Invoke();
            return Matches.Count > 0 ? Matches.Dequeue() : TargetAppTextElementMatch.Same;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
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
