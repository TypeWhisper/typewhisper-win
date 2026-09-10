using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryReaderTests
{
    private static TranscriptionRecord Record(string id, string raw = "raw", string final = "final", string? app = null, int day = 1) => new()
    {
        Id = id, RawText = raw, FinalText = final, AppName = app,
        Timestamp = new DateTime(2026, 9, day, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Mock<IHistoryService> Service(params TranscriptionRecord[] records)
    {
        var service = new Mock<IHistoryService>(MockBehavior.Strict);
        service.Setup(s => s.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        service.SetupGet(s => s.Records).Returns(records);
        return service;
    }

    [Fact]
    public async Task ReadsExistingRecordsWithoutMutationAndSortsDeterministically()
    {
        var service = Service(Record("b"), Record("a"), Record("new", day: 2));
        var result = await new HistoryReader(service.Object).ReadAsync();
        Assert.Equal(new[] { "new", "a", "b" }, result.Select(r => r.Id));
        service.Verify(s => s.EnsureLoadedAsync(), Times.Once);
        service.VerifyGet(s => s.Records, Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(" dictated ", null, "one")]
    [InlineData("CLEAN", "editor", "one")]
    [InlineData("clean", "mail", "two")]
    [InlineData("missing", null, null)]
    public async Task CombinesRawFinalTextAndAppFilters(string query, string? app, string? expected)
    {
        var service = Service(Record("one", "dictated", "clean", "Editor"), Record("two", "other", "clean", "Mail"));
        var result = await new HistoryReader(service.Object).ReadAsync(query, app);
        Assert.Equal(expected is null ? Array.Empty<string>() : new[] { expected }, result.Select(r => r.Id));
    }

    [Fact]
    public async Task EmptyHistoryStaysEmpty()
    {
        Assert.Empty(await new HistoryReader(Service().Object).ReadAsync());
    }

    [Fact]
    public async Task CancellationBeforeLoadingDoesNotTouchService()
    {
        var service = new Mock<IHistoryService>(MockBehavior.Strict);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HistoryReader(service.Object).ReadAsync(cancellationToken: new CancellationToken(true)));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadFailureIsNotDisguisedAsEmptyHistory()
    {
        var service = new Mock<IHistoryService>(MockBehavior.Strict);
        service.Setup(s => s.EnsureLoadedAsync()).ThrowsAsync(new IOException("fixture"));
        await Assert.ThrowsAsync<IOException>(() => new HistoryReader(service.Object).ReadAsync());
    }
}
