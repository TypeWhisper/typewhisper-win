using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Linear;

/// <summary>
/// Provides linear plugin behavior.
/// </summary>
public sealed class LinearPlugin : IActionPlugin
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient = new();
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _defaultTeamId;
    private string? _defaultProjectId;

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.linear";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Linear";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Gets the action id.
    /// </summary>
    public string ActionId => "create-linear-issue";
    /// <summary>
    /// Gets the action name.
    /// </summary>
    public string ActionName => "Create Linear Issue";
    /// <summary>
    /// Gets the action icon.
    /// </summary>
    public string? ActionIcon => "\U0001F4CB";

    /// <summary>
    /// Gets the host.
    /// </summary>
    public IPluginHostServices? Host => _host;
    /// <summary>
    /// Gets the api key.
    /// </summary>
    public string? ApiKey => _apiKey;
    /// <summary>
    /// Gets the default team id.
    /// </summary>
    public string? DefaultTeamId => _defaultTeamId;
    /// <summary>
    /// Gets the default project id.
    /// </summary>
    public string? DefaultProjectId => _defaultProjectId;

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _defaultTeamId = host.GetSetting<string>("default-team-id");
        _defaultProjectId = host.GetSetting<string>("default-project-id");

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        host.Log(PluginLogLevel.Info, "Linear plugin activated");
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host?.Log(PluginLogLevel.Info, "Linear plugin deactivated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs execute asynchronously.
    /// </summary>
    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ActionResult(false, "Linear API key not configured. Please set it in plugin settings.");

        if (string.IsNullOrWhiteSpace(_defaultTeamId))
            return new ActionResult(false, "Default team ID not configured. Please set it in plugin settings.");

        var title = ExtractTitle(input);
        var description = input;

        try
        {
            var issueUrl = await CreateIssueAsync(title, description, ct);

            if (issueUrl is not null)
                return new ActionResult(true, $"Linear issue created: {title}", Url: issueUrl, DisplayDuration: 5.0);

            return new ActionResult(false, "Failed to create Linear issue. Check logs for details.");
        }
        catch (OperationCanceledException)
        {
            return new ActionResult(false, "Issue creation was cancelled.");
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Error, $"Failed to create Linear issue: {ex.Message}");
            return new ActionResult(false, $"Error creating issue: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new LinearSettingsView(this);

    /// <summary>
    /// Saves api key asynchronously..
    /// </summary>
    public async Task SaveApiKeyAsync(string apiKey)
    {
        if (_host is null) return;
        _apiKey = apiKey;
        await _host.StoreSecretAsync("api-key", apiKey);
        _host.Log(PluginLogLevel.Info, "Linear API key saved");
    }

    /// <summary>
    /// Saves default team id.
    /// </summary>
    public void SaveDefaultTeamId(string teamId)
    {
        _defaultTeamId = string.IsNullOrWhiteSpace(teamId) ? null : teamId.Trim();
        _host?.SetSetting("default-team-id", _defaultTeamId ?? "");
    }

    /// <summary>
    /// Saves default project id.
    /// </summary>
    public void SaveDefaultProjectId(string projectId)
    {
        _defaultProjectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        _host?.SetSetting("default-project-id", _defaultProjectId ?? "");
    }

    /// <summary>
    /// Fetches teams asynchronously..
    /// </summary>
    public async Task<List<LinearTeam>> FetchTeamsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return [];

        const string query = """
            query {
                teams {
                    nodes {
                        id
                        name
                        key
                    }
                }
            }
            """;

        var response = await SendGraphQlAsync(query, ct);
        if (response is null) return [];

        try
        {
            var data = response.Value.GetProperty("data").GetProperty("teams").GetProperty("nodes");
            var teams = new List<LinearTeam>();

            foreach (var node in data.EnumerateArray())
            {
                teams.Add(new LinearTeam
                {
                    Id = node.GetProperty("id").GetString() ?? "",
                    Name = node.GetProperty("name").GetString() ?? "",
                    Key = node.GetProperty("key").GetString() ?? ""
                });
            }

            return teams;
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Failed to parse teams response: {ex.Message}");
            return [];
        }
    }

    private async Task<string?> CreateIssueAsync(string title, string description, CancellationToken ct)
    {
        var variables = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description,
            ["teamId"] = _defaultTeamId
        };

        if (!string.IsNullOrWhiteSpace(_defaultProjectId))
            variables["projectId"] = _defaultProjectId;

        const string mutation = """
            mutation IssueCreate($title: String!, $description: String, $teamId: String!, $projectId: String) {
                issueCreate(input: {
                    title: $title
                    description: $description
                    teamId: $teamId
                    projectId: $projectId
                }) {
                    success
                    issue {
                        id
                        identifier
                        url
                    }
                }
            }
            """;

        var response = await SendGraphQlAsync(mutation, ct, variables);
        if (response is null) return null;

        try
        {
            var issueCreate = response.Value.GetProperty("data").GetProperty("issueCreate");
            var success = issueCreate.GetProperty("success").GetBoolean();

            if (!success)
            {
                _host?.Log(PluginLogLevel.Warning, "Linear API returned success=false for issueCreate");
                return null;
            }

            var issue = issueCreate.GetProperty("issue");
            var url = issue.GetProperty("url").GetString();
            var identifier = issue.GetProperty("identifier").GetString();

            _host?.Log(PluginLogLevel.Info, $"Created Linear issue {identifier}");
            return url;
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Failed to parse issue creation response: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> SendGraphQlAsync(
        string query,
        CancellationToken ct,
        Dictionary<string, object?>? variables = null)
    {
        var payload = new Dictionary<string, object?> { ["query"] = query };

        if (variables is not null)
            payload["variables"] = variables;

        var json = JsonSerializer.Serialize(payload, s_jsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linear.app/graphql");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _host?.Log(PluginLogLevel.Error, $"Linear API error {(int)response.StatusCode}: {errorBody}");
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);

        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            var errorMsg = errors.EnumerateArray().FirstOrDefault().GetProperty("message").GetString();
            _host?.Log(PluginLogLevel.Error, $"Linear GraphQL error: {errorMsg}");
            return null;
        }

        return doc.RootElement;
    }

    private static string ExtractTitle(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Untitled Issue";

        // Use the first line as the title
        var firstLine = input.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
            return "Untitled Issue";

        // Truncate to 100 characters
        return firstLine.Length > 100 ? firstLine[..100] : firstLine;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Provides linear team behavior.
/// </summary>
public sealed class LinearTeam
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public string Id { get; init; } = "";
    /// <summary>
    /// Gets or sets the name value.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// Gets or sets the key value.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// Converts to string.
    /// </summary>
    public override string ToString() => $"{Key} - {Name}";
}
