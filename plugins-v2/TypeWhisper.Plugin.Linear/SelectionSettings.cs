using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Linear;

public sealed partial class LinearPlugin
{
    internal sealed record SelectionCatalog(string KeyFingerprint, PluginSettingChoice[] Teams, PluginSettingChoice[] Projects);
    private SelectionCatalog? _catalog;
    private string KeyFingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Connection.Key ?? "")));
    private SelectionCatalog? CurrentCatalog => Connection.Configured && _catalog?.KeyFingerprint == KeyFingerprint ? _catalog : null;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        new("teamId", Connection.L("Team", "Team"),
            Connection.L("Load teams and projects, then choose where workflow issues are created.",
                "Lade Teams und Projekte und wähle, wo Workflow-Issues erstellt werden."), Connection.Get("teamId"))
        { Choices = Choices(CurrentCatalog?.Teams, "teamId", Connection.L("Select a team…", "Team auswählen…")) },
        new("projectId", Connection.L("Project (optional)", "Projekt (optional)"),
            Connection.L("Choose a project belonging to the selected team, or leave issues without a project.",
                "Wähle ein Projekt des ausgewählten Teams oder erstelle Issues ohne Projekt."), Connection.Get("projectId"))
        { Choices = Choices(CurrentCatalog?.Projects, "projectId", Connection.L("No project", "Kein Projekt")) }
    ];

    private PluginSettingChoice[] Choices(PluginSettingChoice[]? loaded, string setting, string emptyTitle)
    {
        var choices = new List<PluginSettingChoice> { new("", emptyTitle) };
        choices.AddRange(loaded ?? []);
        var saved = Connection.Get(setting);
        if (saved.Length > 0 && !choices.Any(c => c.Value == saved))
            choices.Add(new(saved, Connection.L($"Saved selection ({saved})", $"Gespeicherte Auswahl ({saved})")));
        return choices.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions =>
    [new("refresh-selections", Connection.L("Load teams and projects", "Teams und Projekte laden"),
        Connection.L("Uses the saved API key. This does not create an issue.",
            "Verwendet den gespeicherten API-Schlüssel. Dabei wird kein Issue erstellt."))];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id != "refresh-selections") throw new ArgumentException("Unknown action.", nameof(id));
        ct.ThrowIfCancellationRequested();
        _ = Connection.RequireKey();
        var fingerprint = KeyFingerprint;
        var teams = await LoadChoicesAsync("teams", ct);
        var projects = await LoadChoicesAsync("projects", ct);
        ct.ThrowIfCancellationRequested();
        if (fingerprint != KeyFingerprint || !Connection.Configured)
            throw new InvalidOperationException("The connection changed. Load teams and projects again.");
        var catalog = new SelectionCatalog(fingerprint, teams, projects);
        Connection.Host!.SetSetting("selectionCatalog", catalog);
        _catalog = catalog;
        Connection.Host.NotifyCapabilitiesChanged();
        return Connection.L($"Loaded {teams.Length} teams and {projects.Length} projects. Select a team and save settings.",
            $"{teams.Length} Teams und {projects.Length} Projekte geladen. Wähle ein Team und speichere die Einstellungen.");
    }

    private async Task<PluginSettingChoice[]> LoadChoicesAsync(string collection, CancellationToken ct)
    {
        var choices = new Dictionary<string, PluginSettingChoice>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            using var document = await GraphQlAsync(
                $"query Choices($after: String) {{ {collection}(first: 100, after: $after) {{ nodes {{ id name }} pageInfo {{ hasNextPage endCursor }} }} }}",
                new { after = cursor }, ct);
            var data = ProviderConnection.Required(document.RootElement, "data", JsonValueKind.Object);
            var connection = ProviderConnection.Required(data, collection, JsonValueKind.Object);
            foreach (var node in ProviderConnection.Required(connection, "nodes", JsonValueKind.Array).EnumerateArray())
            {
                var value = ProviderConnection.RequiredText(node, "id");
                var name = ProviderConnection.RequiredText(node, "name");
                if (!Guid.TryParse(value, out _) || name.Length > 512) throw ProviderConnection.InvalidResponse();
                choices[value] = new(value, name);
            }
            var info = ProviderConnection.Required(connection, "pageInfo", JsonValueKind.Object);
            if (!info.TryGetProperty("hasNextPage", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw ProviderConnection.InvalidResponse();
            if (!more.GetBoolean())
                return choices.Values.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Value).ToArray();
            cursor = ProviderConnection.RequiredText(info, "endCursor");
            if (!cursors.Add(cursor)) throw ProviderConnection.InvalidResponse();
        }
        throw new InvalidOperationException("Too many teams or projects. The previous selection list was retained.");
    }
}
