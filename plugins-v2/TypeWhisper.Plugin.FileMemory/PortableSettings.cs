using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

public sealed partial class FileMemoryPlugin : IActionPlugin, IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private string L(string english, string german)
    {
        var language = PortableLocalization.TryGet(_host)?.CurrentLanguage ?? CultureInfo.CurrentUICulture.Name;
        return language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? german : english;
    }
    private string SelectedId
    {
        get
        {
            var selected = _editing;
            return selected is not null && (selected == _draftId || _entries.Any(e => e.Id.ToString() == selected))
                ? selected : _entries.FirstOrDefault()?.Id.ToString() ?? "none";
        }
    }
    private static string Label(string text)
    {
        var firstLine = text.Split(['\r', '\n'], 2)[0];
        return firstLine.Length > 65 ? firstLine[..65] + "…" : firstLine;
    }
    /// <inheritdoc />
    public string? ActionIcon => "file";
    /// <inheritdoc />
    public string ActionId => "store-" + PluginId;
    /// <inheritdoc />
    public string ActionName => L("Remember in File Memory", "In File Memory merken");
    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    {
        await StoreAsync(input, ct).ConfigureAwait(false);
        return new(true, L("Memory stored locally.", "Erinnerung lokal gespeichert."));
    }
    /// <inheritdoc />
    public string ProfileSelectorId => "memory";
    /// <inheritdoc />
    public string? AddProfileActionId => _loadError is null ? "add" : null;
    /// <inheritdoc />
    public string? RemoveProfileActionId => _loadError is null && SelectedId != "none" ? "remove:" + SelectedId : null;
    /// <inheritdoc />
    public bool ShowApiKeySettings => false;
    /// <inheritdoc />
    public string ConnectionIdentity => SelectedId;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            if (_loadError is not null)
                return [new(ProfileSelectorId, L("Memories", "Erinnerungen"), L(_loadError,
                    "Die Erinnerungsdatei konnte nicht geladen werden. Stelle memories.json wieder her oder repariere sie und lade das Plugin erneut. Die Datei bleibt unverändert."), "none"),
                    new("load_error", L("Memory file unavailable", "Erinnerungsdatei nicht verfügbar"),
                        L(_loadError, "Stelle memories.json wieder her oder repariere sie und lade das Plugin erneut. Die vorhandene Datei bleibt erhalten."), "readonly")
                    { Section = PluginSettingsSection.Connection, Choices = [new("readonly", L("Read-only", "Schreibgeschützt"))] }];
            var entries = _entries;
            var selected = SelectedId;
            var choices = entries.OrderByDescending(e => e.CreatedAt)
                .Select(e => new PluginSettingChoice(e.Id.ToString(), Label(e.Content))).ToList();
            if (_draftId is { } draft) choices.Add(new(draft, L("New memory", "Neue Erinnerung")));
            var fields = new List<PluginTextSetting>
            {
                new(ProfileSelectorId, L("Memories", "Erinnerungen"), L("Select an entry to edit or remove it.", "Eintrag zum Bearbeiten oder Löschen auswählen."), selected) { Choices = choices }
            };
            if (selected != "none")
                fields.Add(new(selected + ":content", L("Memory", "Erinnerung"),
                    L($"{entries.Length} saved locally. Add entries here or with a workflow action. Automatic extraction and recall are not enabled.",
                        $"{entries.Length} lokal gespeichert. Einträge hier oder per Workflow-Aktion hinzufügen. Automatisches Extrahieren und Abrufen ist nicht aktiviert."),
                    entries.FirstOrDefault(e => e.Id.ToString() == selected)?.Content ?? "")
                    { IsMultiline = true, Section = PluginSettingsSection.Connection });
            fields.Add(new("query", L("Search memories", "Erinnerungen suchen"), L("Search the saved entries without saving your draft.", "Gespeicherte Einträge durchsuchen, ohne den Entwurf zu speichern."), _query, 1000)
                { Section = PluginSettingsSection.Connection });
            return fields;
        }
    }

    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        if (id == ProfileSelectorId)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RequireWritable();
                if (value != _draftId && !_entries.Any(e => e.Id.ToString() == value)) throw new ArgumentException("Unknown memory.");
                _editing = value;
            }
            finally { _lock.Release(); }
        }
        else await SaveProfileSettingsAsync(SelectedId, new Dictionary<string, string> { [id] = value }, null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWritable();
            if (profileId != SelectedId) throw new ArgumentException("The selected memory changed. Reopen its settings.");
            var query = _query;
            string? content = null;
            foreach (var (key, value) in values)
            {
                if (key == "query" && value.Length <= 1000) query = value;
                else if (key == profileId + ":content" && profileId != "none") content = ValidateContent(value);
                else throw new ArgumentException("Invalid memory setting.");
            }
            if (content is not null)
            {
                var id = Guid.Parse(profileId);
                if (_entries.Any(e => e.Id != id && e.Content == content))
                    throw new ArgumentException(L("This memory already exists.", "Diese Erinnerung ist bereits vorhanden."));
                var existing = _entries.FirstOrDefault(e => e.Id == id);
                var replacement = existing is null
                    ? [.. _entries, new MemoryEntry(content, DateTime.UtcNow) { Id = id }]
                    : _entries.Select(e => e.Id == id ? e with { Content = content } : e).ToArray();
                await PersistAsync(replacement, ct).ConfigureAwait(false);
                if (_draftId == profileId) _draftId = null;
            }
            _query = query;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            if (_loadError is not null) return [];
            var actions = new List<PluginSettingsAction> { Action("add", L("Add memory", "Erinnerung hinzufügen")),
                Action("search", L("Search", "Suchen")) };
            if (RemoveProfileActionId is { } remove) actions.Add(Action(remove, L("Remove memory…", "Erinnerung entfernen…")));
            return actions;
        }
    }
    private static PluginSettingsAction Action(string id, string title) => new(id, title, "") { Section = PluginSettingsSection.Connection };

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWritable();
            if (id == "add")
            {
                _draftId ??= Guid.NewGuid().ToString();
                _editing = _draftId;
                return L("Enter a memory, then save it.", "Erinnerung eingeben und speichern.");
            }
            if (id == "remove:" + SelectedId && SelectedId != "none")
            {
                var selected = SelectedId;
                if (selected == _draftId) _draftId = null;
                else await PersistAsync(_entries.Where(e => e.Id.ToString() != selected).ToArray(), ct).ConfigureAwait(false);
                _editing = _entries.FirstOrDefault()?.Id.ToString();
                return L("Memory removed.", "Erinnerung entfernt.");
            }
            throw new ArgumentException("Unknown or stale memory action.");
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        if (profileId != SelectedId) throw new ArgumentException("The selected memory changed.");
        if (actionId != "search") throw new ArgumentException("Unknown memory action.");
        var query = values.GetValueOrDefault("query", _query);
        if (query.Length > 1000) throw new ArgumentException("Search is too long.");
        var results = await SearchAsync(query, 10, ct).ConfigureAwait(false);
        var text = results.Count == 0 ? L("No memories found.", "Keine Erinnerungen gefunden.") : string.Join("\n\n", results);
        if (text.Length > 4000) text = text[..4000] + "…";
        return new(text);
    }
}
