using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryWorkspaceTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    // Wednesday, 30 September 2026, 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static TranscriptionRecord Record(string id, DateTime at, string text = "text", string? source = "dictation",
        string? app = null, string? audio = null, TranscriptionRecordStatus status = TranscriptionRecordStatus.Succeeded,
        string? inbox = null, double duration = 1, string? raw = null) => new()
        {
            Id = id, Timestamp = at, RawText = raw ?? text, FinalText = text, SourceKind = source, AppName = app,
            AudioFileName = audio, Status = status, InboxState = inbox, DurationSeconds = duration
        };

    [Fact]
    public void MailboxesMatchMacOsMembership()
    {
        var local = Record("a", Now.UtcDateTime);
        var open = Record("b", Now.UtcDateTime, inbox: HistoryWorkspace.InboxOpen);
        var done = Record("c", Now.UtcDateTime, inbox: HistoryWorkspace.InboxCompleted);
        var audio = Record("d", Now.UtcDateTime, audio: "d.wav");
        var failed = Record("e", Now.UtcDateTime, status: TranscriptionRecordStatus.TextProcessorFailed);
        Assert.False(HistoryWorkspace.InMailbox(local, HistoryMailbox.Inbox));
        Assert.True(HistoryWorkspace.InMailbox(open, HistoryMailbox.Inbox));
        Assert.False(HistoryWorkspace.InMailbox(done, HistoryMailbox.Inbox));
        Assert.True(HistoryWorkspace.InMailbox(audio, HistoryMailbox.WithAudio));
        Assert.False(HistoryWorkspace.InMailbox(local, HistoryMailbox.WithAudio));
        Assert.True(HistoryWorkspace.InMailbox(failed, HistoryMailbox.Failed));
        Assert.True(HistoryWorkspace.InMailbox(done, HistoryMailbox.All));
    }

    [Fact]
    public void SourceScopeTreatsLegacyEntriesAsDictation()
    {
        var legacy = Record("a", Now.UtcDateTime, source: null);
        var recording = Record("b", Now.UtcDateTime, source: "recording");
        var scope = new HistoryScope(null, "dictation");
        var result = HistoryWorkspace.Query([legacy, recording], scope, null, HistoryDateRange.AllTime, null, HistorySort.NewestFirst, Now, Zone);
        Assert.Equal(["a"], result.Select(record => record.Id));
    }

    [Fact]
    public void DeviceScopesSeparateThisPcFromSyncedDevices()
    {
        var local = Record("local", Now.UtcDateTime);
        var ownPublished = Record("own", Now.UtcDateTime) with { OriginDeviceId = "this-pc" };
        var mac = Record("mac", Now.UtcDateTime) with { OriginDeviceId = "mac-1", OriginPlatform = "macOS" };
        IEnumerable<string> Ids(HistoryScope scope) => HistoryWorkspace.Query([local, ownPublished, mac], scope, null, HistoryDateRange.AllTime, null,
            HistorySort.NewestFirst, Now, Zone, "this-pc").Select(record => record.Id).Order();
        Assert.Equal(["local", "own"], Ids(new HistoryScope(null)));
        Assert.Equal(["mac"], Ids(new HistoryScope(null, Device: "mac-1")));
        Assert.Equal(["local", "mac", "own"], Ids(HistoryScope.All));
    }

    [Fact]
    public void QueryFiltersBySearchDateAndAppThenSorts()
    {
        var records = new[]
        {
            Record("recent", Now.UtcDateTime.AddDays(-1), "Meeting notes", app: "Outlook", duration: 5),
            Record("old", Now.UtcDateTime.AddDays(-40), "Meeting agenda", app: "Teams", duration: 50),
            Record("other", Now.UtcDateTime, "Groceries", app: "outlook", duration: 1)
        };
        Assert.Equal(["recent", "old"], HistoryWorkspace.Query(records, HistoryScope.All, "meeting", HistoryDateRange.AllTime, null, HistorySort.NewestFirst, Now, Zone).Select(r => r.Id));
        Assert.Equal(["recent"], HistoryWorkspace.Query(records, HistoryScope.All, "meeting", HistoryDateRange.Last30Days, null, HistorySort.NewestFirst, Now, Zone).Select(r => r.Id));
        Assert.Equal(["other", "recent"], HistoryWorkspace.Query(records, HistoryScope.All, null, HistoryDateRange.AllTime, "OUTLOOK", HistorySort.NewestFirst, Now, Zone).Select(r => r.Id));
        Assert.Equal(["old", "recent", "other"], HistoryWorkspace.Query(records, HistoryScope.All, null, HistoryDateRange.AllTime, null, HistorySort.Duration, Now, Zone).Select(r => r.Id));
        Assert.Equal(["old", "recent", "other"], HistoryWorkspace.Query(records, HistoryScope.All, null, HistoryDateRange.AllTime, null, HistorySort.OldestFirst, Now, Zone).Select(r => r.Id));
    }

    [Fact]
    public void SearchMatchesOriginalTextToo()
    {
        var record = Record("a", Now.UtcDateTime, "Final wording", raw: "spoken draft");
        Assert.Single(HistoryWorkspace.Query([record], HistoryScope.All, "draft", HistoryDateRange.AllTime, null, HistorySort.NewestFirst, Now, Zone));
    }

    [Theory]
    [InlineData(0, HistoryDateGroup.Today)]
    [InlineData(-1, HistoryDateGroup.Yesterday)]
    [InlineData(-2, HistoryDateGroup.ThisWeek)] // Monday of the same week
    [InlineData(-3, HistoryDateGroup.ThisMonth)] // previous Sunday
    [InlineData(-30, HistoryDateGroup.Older)]
    public void GroupsFollowCalendarDaysWithMondayWeeks(int days, HistoryDateGroup expected) =>
        Assert.Equal(expected, HistoryWorkspace.GroupOf(Now.UtcDateTime.AddDays(days), Now, Zone));

    [Fact]
    public void PreviousMonthIsOlderEvenWithinTheLastWeek()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero); // Thursday
        Assert.Equal(HistoryDateGroup.ThisWeek, HistoryWorkspace.GroupOf(now.UtcDateTime.AddDays(-2), now, Zone));
        Assert.Equal(HistoryDateGroup.Older, HistoryWorkspace.GroupOf(now.UtcDateTime.AddDays(-5), now, Zone));
    }

    [Fact]
    public void WordDiffMarksRemovedAndAddedWords()
    {
        var diff = HistoryWorkspace.WordDiff("the quick brown fox", "the slow brown fox jumps")!;
        Assert.Equal(
        [
            ("the ", HistoryDiffKind.Unchanged), ("quick ", HistoryDiffKind.Removed), ("slow ", HistoryDiffKind.Added),
            ("brown ", HistoryDiffKind.Unchanged), ("fox ", HistoryDiffKind.Unchanged), ("jumps", HistoryDiffKind.Added)
        ], diff.Select(token => (token.Text, token.Kind)));
    }

    [Fact]
    public void WordDiffIsUnavailableForVeryLongTexts()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", HistoryWorkspace.MaximumDiffWords + 1));
        Assert.Null(HistoryWorkspace.WordDiff(text, "short"));
    }

    [Fact]
    public void ProcessedEntriesAreDetectedIgnoringSurroundingWhitespace()
    {
        Assert.False(HistoryWorkspace.WasProcessed(Record("a", Now.UtcDateTime, "same ", raw: "same")));
        Assert.True(HistoryWorkspace.WasProcessed(Record("b", Now.UtcDateTime, "Same.", raw: "same")));
    }
}
