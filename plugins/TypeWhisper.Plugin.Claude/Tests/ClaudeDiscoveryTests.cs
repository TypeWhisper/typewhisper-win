using System.Net;
using System.Text.Json;
using TypeWhisper.PluginSDK;

public partial class ClaudeTests
{
    [Fact]
    public async Task PaginationFollowsCursorAndCommitsOnlyTheCompleteCatalog()
    {
        var calls = 0;
        using var plugin = Plugin((request, _) =>
        {
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Equal("fixture-key", request.Headers.GetValues("x-api-key").Single());
            if (++calls == 1)
            {
                Assert.Equal("?limit=1000", request.RequestUri!.Query);
                return Task.FromResult(Json("""{"data":[{"id":"newest"}],"has_more":true,"last_id":"cursor/one"}"""));
            }
            Assert.Equal("?limit=1000&after_id=cursor%2Fone", request.RequestUri!.Query);
            return Task.FromResult(Json("""{"data":[{"id":"older"},{"id":"newest"}],"has_more":false}"""));
        });
        await Configure(plugin, new());
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "newest");
        await plugin.SaveProfileSettingsAsync("claude", Empty, null, default);
        Assert.Equal(new[] { "newest", "older", "claude-sonnet-5" }, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedCursorAndUnboundedPaginationPreserveSavedCatalog(bool uniqueCursors)
    {
        var calls = 0;
        using var plugin = Plugin((_, _) =>
        {
            var cursor = uniqueCursors ? "cursor-" + ++calls : "repeated";
            return Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                data = new[] { new { id = "discovered" } }, has_more = true, last_id = cursor
            })));
        });
        var host = new TestPluginHostServices();
        await Configure(plugin, host);
        var writes = host.SettingWrites;
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.Equal(writes, host.SettingWrites);
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "discovered");
        if (uniqueCursors) Assert.Equal(20, calls);
    }

    [Fact]
    public async Task FailedSecondPageDoesNotStagePartialModels()
    {
        var calls = 0;
        using var plugin = Plugin((_, _) => Task.FromResult(++calls == 1
            ? Json("""{"data":[{"id":"partial"}],"has_more":true,"last_id":"partial"}""")
            : Json("""{"error":{"message":"unavailable"}}""", HttpStatusCode.ServiceUnavailable)));
        await Configure(plugin, new());
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.DoesNotContain(plugin.TextSettings[1].Choices, c => c.Value == "partial");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteRefreshCannotStageModelsAfterKeyChangeOrDeactivation(bool deactivate)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = Plugin(async (_, _) =>
        {
            entered.SetResult(); await finish.Task;
            return Json("""{"data":[{"id":"obsolete"}],"has_more":false}""");
        });
        await Configure(plugin, new());
        var pending = plugin.ExecuteSettingsActionAsync("refreshModels", default);
        await entered.Task;
        if (deactivate) await plugin.DeactivateAsync(); else await plugin.SetApiKeyAsync("replacement");
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain(plugin.TextSettings[1].Choices, c => c.Value == "obsolete");
    }

    [Theory]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-opus-5", false)]
    [InlineData("claude-future", false)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5-20251001", true)]
    public async Task TemperatureIsSentOnlyForSupportedModels(string model, bool supported)
    {
        using var plugin = Plugin(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(supported, doc.RootElement.TryGetProperty("temperature", out var temperature));
            if (supported) Assert.Equal(0.3, temperature.GetDouble());
            return Json(Answer);
        });
        await Configure(plugin, new());
        await plugin.SaveProfileSettingsAsync("claude", new Dictionary<string, string> { ["llmTemperatureMode"] = "custom" }, null, default);
        await plugin.ProcessAsync("", "hello", model, default);
    }

    [Fact]
    public async Task RetiredExplicitModelIsNotSilentlyReplacedAndExplainsRecovery()
    {
        using var plugin = Plugin(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("claude-sonnet-4-20250514", doc.RootElement.GetProperty("model").GetString());
            return Json("""{"error":{"type":"not_found_error"}}""", HttpStatusCode.NotFound);
        });
        await Configure(plugin, new());
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "claude-sonnet-4-20250514");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "hello", "claude-sonnet-4-20250514", default));
        Assert.Equal(404, error.HttpStatusCode);
        Assert.Contains("Refresh models", error.Message);
        Assert.False(error.IsTransient);
    }
}
