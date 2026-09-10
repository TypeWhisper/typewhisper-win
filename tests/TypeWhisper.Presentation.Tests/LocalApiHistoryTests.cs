using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiHistoryTests
{
    private static TranscriptionRecord Entry(string text, int minute = 0) => new()
    {
        Id = Guid.NewGuid().ToString(), RawText = "Original " + text, FinalText = text,
        Timestamp = new DateTime(2026, 9, 8, 10, minute, 0, DateTimeKind.Utc),
        AppName = "Editor", AppProcessName = "editor.exe", AppUrl = "https://example.org/private-path",
        DurationSeconds = 2.5, Language = "de", EngineUsed = "test-engine", ModelUsed = "test-model"
    };

    private static (LocalApiHistory Api, Mock<IHistoryService> Service) Create(params TranscriptionRecord[] records)
    {
        var service = new Mock<IHistoryService>(MockBehavior.Strict);
        service.Setup(s => s.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        service.SetupGet(s => s.Records).Returns(() => records);
        return (new LocalApiHistory(new HistoryReader(service.Object), new HistoryActions(service.Object)), service);
    }

    private static LocalApiRequest Request(string method = "GET", params (string Key, string? Value)[] query) =>
        new(method, "/v1/history", [], null, query.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Fact]
    public async Task ListReturnsStableSnakeCaseContractInNewestOrder()
    {
        var first = Entry("Two words", 1);
        var (api, _) = Create(Entry("older"), first);
        var response = await api.HandleAsync(Request());
        Assert.Equal(200, response.StatusCode);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(50, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        var entry = root.GetProperty("entries")[0];
        Assert.Equal(first.Id, entry.GetProperty("id").GetString());
        Assert.Equal(first.FinalText, entry.GetProperty("text").GetString());
        Assert.Equal(first.RawText, entry.GetProperty("raw_text").GetString());
        Assert.Equal(first.Timestamp, entry.GetProperty("timestamp").GetDateTime());
        Assert.Equal("Editor", entry.GetProperty("app_name").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("app_bundle_id").ValueKind);
        Assert.Equal(first.AppUrl, entry.GetProperty("app_url").GetString());
        Assert.Equal(2.5, entry.GetProperty("duration").GetDouble());
        Assert.Equal("de", entry.GetProperty("language").GetString());
        Assert.Equal("test-engine", entry.GetProperty("engine").GetString());
        Assert.Equal("test-model", entry.GetProperty("model").GetString());
        Assert.Equal(2, entry.GetProperty("words_count").GetInt32());
        Assert.Equal(12, entry.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(" original ")]
    [InlineData("EdItOr")]
    [InlineData("EXAMPLE.ORG")]
    public async Task SearchIncludesTextAppAndDomainBeforePagination(string term)
    {
        var (api, _) = Create(Entry("older"), Entry("newer", 1));
        var response = await api.HandleAsync(Request("GET", ("q", term), ("limit", "1"), ("offset", "1")));
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("older", Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("-1", "-2", 0, 0)]
    [InlineData("9999999999", "9999999999", 200, 9999999999)]
    [InlineData("garbage", "garbage", 50, 0)]
    [InlineData(" 5", "1.5", 50, 0)]
    [InlineData("9223372036854775808", "9223372036854775808", 50, 0)]
    public async Task PaginationUsesMacDefaultsAndClamping(string limit, string offset, int expectedLimit, long expectedOffset)
    {
        var (api, _) = Create();
        var response = await api.HandleAsync(Request("GET", ("limit", limit), ("offset", offset)));
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(expectedLimit, json.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(expectedOffset, json.RootElement.GetProperty("offset").GetInt64());
        Assert.Empty(json.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteReportsPersistenceOutcomeAndUsesStoredIdentity(bool persisted)
    {
        var entry = Entry("keep until saved");
        var (api, service) = Create(entry);
        service.Setup(s => s.DeleteRecord(entry.Id)).Callback(() =>
        {
            if (persisted) service.SetupGet(s => s.Records).Returns(Array.Empty<TranscriptionRecord>());
        });
        var response = await api.HandleAsync(Request("DELETE", ("id", entry.Id.ToUpperInvariant())));
        Assert.Equal(persisted ? 200 : 500, response.StatusCode);
        service.Verify(s => s.DeleteRecord(entry.Id), Times.Once);
        using var json = JsonDocument.Parse(response.Body);
        if (persisted) Assert.True(json.RootElement.GetProperty("deleted").GetBoolean());
        else Assert.Equal("error", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeletePreservesOpaqueWindowsIdentities()
    {
        var entry = Entry("text") with { Id = "existing-opaque-id" };
        var (api, service) = Create(entry);
        service.Setup(s => s.DeleteRecord(entry.Id)).Callback(() =>
            service.SetupGet(s => s.Records).Returns(Array.Empty<TranscriptionRecord>()));
        Assert.Equal(200, (await api.HandleAsync(Request("DELETE", ("id", entry.Id)))).StatusCode);
    }

    [Theory]
    [InlineData(null, 400)]
    [InlineData("", 400)]
    [InlineData("not-a-uuid", 400)]
    [InlineData("73cf1213-1d9f-44ef-9568-87d45017902f", 404)]
    public async Task InvalidOrMissingDeleteCannotMutateHistory(string? id, int expectedStatus)
    {
        var (api, service) = Create(Entry("retain"));
        var response = await api.HandleAsync(Request("DELETE", ("id", id)));
        Assert.Equal(expectedStatus, response.StatusCode);
        service.Verify(s => s.DeleteRecord(It.IsAny<string>()), Times.Never);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(expectedStatus == 400 ? "bad_request" : "not_found",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CancellationDuringLoadCannotDeleteLater()
    {
        var entry = Entry("retain");
        var (api, service) = Create(entry);
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(s => s.EnsureLoadedAsync()).Returns(loading.Task);
        using var cancellation = new CancellationTokenSource();
        var request = api.HandleAsync(Request("DELETE", ("id", entry.Id)), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        loading.SetResult();
        service.Verify(s => s.DeleteRecord(It.IsAny<string>()), Times.Never);
    }
}
