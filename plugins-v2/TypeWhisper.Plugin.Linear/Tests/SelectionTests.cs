using System.Text.Json;
using TypeWhisper.Plugin.Linear;

public sealed partial class ProviderTests
{
    private const string Team = "12345678-1234-1234-1234-123456789abc";

    [Fact]
    public async Task IssueTitleSkipsWhitespaceLinesAndPreservesFullDescription()
    {
        const string text = " \r\n  A useful title  \nFull description";
        using var http = new HttpClient(new Handler((_, body) =>
        {
            using var request = JsonDocument.Parse(body!);
            var input = request.RootElement.GetProperty("variables").GetProperty("input");
            Assert.Equal("A useful title", input.GetProperty("title").GetString());
            Assert.Equal(text, input.GetProperty("description").GetString());
            return Json("""{"data":{"issueCreate":{"success":true,"issue":{"url":"https://linear.app/team/issue/TST-1"}}}}""");
        }));
        using var plugin = new LinearPlugin(http);
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        Assert.True((await plugin.ExecuteAsync(text, new(null, null, null, null, null), default)).Success);
    }
    private static string ChoicePage(string collection, string name, bool more = false, string? cursor = null) =>
        JsonSerializer.Serialize(new { data = new Dictionary<string, object> { [collection] = new
        { nodes = new[] { new { id = Team, name } }, pageInfo = new { hasNextPage = more, endCursor = cursor } } } });

    [Fact]
    public async Task RefreshLoadsNamedChoicesWithoutMutationAndRestoresThemAfterRestart()
    {
        var host = new Host();
        var calls = 0;
        using var http = new HttpClient(new Handler((_, body) =>
        {
            Assert.DoesNotContain("mutation", body);
            calls++;
            return Json(body!.Contains("teams(") ? ChoicePage("teams", "TypeWhisper") : ChoicePage("projects", "Windows"));
        }));
        using var plugin = new LinearPlugin(http);
        await plugin.ActivateAsync(host);
        await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refresh-selections", default);
        Assert.Equal(2, calls);
        Assert.Contains(plugin.TextSettings[0].Choices, c => c.Value == Team && c.Title == "TypeWhisper");
        Assert.Contains(plugin.TextSettings[1].Choices, c => c.Value == Team && c.Title == "Windows");
        Assert.All(plugin.TextSettings, field => Assert.False(field.SaveChoiceOnChange));
        await plugin.DeactivateAsync();
        await plugin.ActivateAsync(host);
        Assert.Contains(plugin.TextSettings[0].Choices, c => c.Title == "TypeWhisper");
        await plugin.SetApiKeyAsync("different-account");
        Assert.DoesNotContain(plugin.TextSettings[0].Choices, c => c.Title == "TypeWhisper");
    }

    [Fact]
    public async Task PaginationUsesCursorAndFailureRetainsPriorCatalog()
    {
        var calls = 0;
        var fail = false;
        using var http = new HttpClient(new Handler((_, body) =>
        {
            calls++;
            if (fail) return Json("""{"errors":[{"message":"denied"}]}""");
            if (body!.Contains("projects(")) return Json(ChoicePage("projects", "Windows"));
            using var request = JsonDocument.Parse(body);
            var after = request.RootElement.GetProperty("variables").GetProperty("after");
            if (after.ValueKind == JsonValueKind.Null) return Json(ChoicePage("teams", "First", true, "page-two"));
            Assert.Equal("page-two", after.GetString());
            return Json(ChoicePage("teams", "Latest"));
        }));
        using var plugin = new LinearPlugin(http);
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refresh-selections", default);
        Assert.Equal(3, calls);
        fail = true;
        await Assert.ThrowsAnyAsync<Exception>(() => plugin.ExecuteSettingsActionAsync("refresh-selections", default));
        Assert.Contains(plugin.TextSettings[0].Choices, c => c.Title == "Latest");
    }

    [Fact]
    public async Task RepeatedPaginationCursorIsRejected()
    {
        using var http = new HttpClient(new Handler((_, _) => Json(ChoicePage("teams", "Team", true, "same"))));
        using var plugin = new LinearPlugin(http);
        await plugin.ActivateAsync(new Host());
        await Configure(plugin);
        await Assert.ThrowsAnyAsync<Exception>(() => plugin.ExecuteSettingsActionAsync("refresh-selections", default));
    }
}
