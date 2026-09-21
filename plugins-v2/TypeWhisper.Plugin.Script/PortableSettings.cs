using System.Globalization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

public sealed partial class ScriptPlugin : IPluginProfileSettings, IPluginSettingsActions, IPluginConnectionSettings
{
    private readonly object _settingsLock = new();
    private Guid? _editing;
    private string _template = "uppercase";
    private ScriptService ActiveService => Service ?? throw new InvalidOperationException("Plugin is not active.");
    private bool German => ActiveService.Localization?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true;
    private string L(string english, string german)
    {
        var translated = ActiveService.Localization?.GetString(english);
        return translated is not null && translated != english ? translated : German ? german : english;
    }
    private ScriptEntry? Selected => ActiveService.Scripts.FirstOrDefault(s => s.Id == _editing) ?? ActiveService.Scripts.FirstOrDefault();
    /// <inheritdoc />
    public string ProfileSelectorId => "script";
    /// <inheritdoc />
    public string AddProfileActionId => "add";
    /// <inheritdoc />
    public string? RemoveProfileActionId => Selected is { } s ? "remove:" + s.Id : null;
    /// <inheritdoc />
    public bool ShowApiKeySettings => false;
    /// <inheritdoc />
    public string ConnectionIdentity => Selected?.Id.ToString() ?? "none";

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings
    {
        get
        {
            lock (_settingsLock)
            {
                var script = Selected;
                var fields = new List<PluginTextSetting>
                {
                    new(ProfileSelectorId, L("Scripts", "Skripte"), L("Scripts run in this order.", "Die Skripte werden in dieser Reihenfolge ausgeführt."), ConnectionIdentity)
                    { Choices = ActiveService.Scripts.Select((s, index) => new PluginSettingChoice(s.Id.ToString(), $"{index + 1}. {s.Name}")).ToArray() }
                };
                if (script is not null)
                {
                    fields.AddRange(new PluginTextSetting[]
                    {
                        Field(script, "name", L("Name", "Name"), "", script.Name, 200),
                        Field(script, "command", L("Command", "Befehl"), L("Text arrives on stdin; write the replacement to stdout. Enable only commands you trust.", "Text kommt über stdin; den Ersatztext auf stdout ausgeben. Nur vertrauenswürdige Befehle aktivieren."), script.Command) with { IsMultiline = true },
                        Field(script, "shell", L("Shell", "Shell"), "", script.Shell) with { Choices = ScriptShells.Supported.Select(s => new PluginSettingChoice(s, s)).ToArray() },
                        Field(script, "timeout", L("Timeout (seconds)", "Zeitlimit in Sekunden"), "1–300", script.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), 3),
                        Field(script, "enabled", L("Run after dictation", "Nach dem Diktieren ausführen"), L("This saved command runs after each transcription while the plugin is enabled.", "Dieser gespeicherte Befehl wird bei aktiviertem Plugin nach jedem Diktat ausgeführt."), script.IsEnabled ? "true" : "false") with
                        { Choices = [new("false", L("Off", "Aus")), new("true", L("On", "An"))] }
                    });
                }
                fields.Add(new("template", L("Script template", "Skriptvorlage"), L("Add a separate disabled script. Existing commands stay intact.", "Ein separates, deaktiviertes Skript hinzufügen. Vorhandene Befehle bleiben erhalten."), _template)
                { Section = PluginSettingsSection.Connection, Choices = ScriptTemplates.All.Select(t => new PluginSettingChoice(t.Id, German ? t.GermanName : t.EnglishName)).ToArray() });
                return fields;
            }
        }
    }

    private static PluginTextSetting Field(ScriptEntry script, string key, string title, string description, string value, int maxLength = 32768) =>
        new(script.Id + ":" + key, title, description, value, maxLength) { Section = PluginSettingsSection.Connection };

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        lock (_settingsLock)
        {
            ct.ThrowIfCancellationRequested();
            if (id == ProfileSelectorId)
            {
                if (!Guid.TryParse(value, out var selected) || !ActiveService.Scripts.Any(s => s.Id == selected)) throw new ArgumentException("Unknown script.");
                _editing = selected;
                return Task.CompletedTask;
            }
            if (id == "template") { _template = Template(value).Id; return Task.CompletedTask; }
            return SaveProfileSettingsAsync(ConnectionIdentity, new Dictionary<string, string> { [id] = value }, null, ct);
        }
    }

    private ScriptEntry ReadDraft(string profileId, IReadOnlyDictionary<string, string> values)
    {
        if (!Guid.TryParse(profileId, out var id) || Selected?.Id != id) throw new ArgumentException("The selected script changed. Reopen its settings.");
        var result = ActiveService.Scripts.Single(s => s.Id == id);
        var fields = TextSettings.ToDictionary(f => f.Id);
        foreach (var (key, value) in values)
        {
            if (!fields.TryGetValue(key, out var field) || key == ProfileSelectorId || value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                throw new ArgumentException("Invalid script setting.");
            if (key == "template") continue;
            result = key[(key.IndexOf(':') + 1)..] switch
            {
                "name" when !string.IsNullOrWhiteSpace(value) => result with { Name = value.Trim() },
                "command" => result with { Command = value },
                "shell" => result with { Shell = value },
                "enabled" => result with { IsEnabled = bool.Parse(value) },
                "timeout" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 300 => result with { TimeoutSeconds = seconds },
                _ => throw new ArgumentException("Invalid script setting.")
            };
        }
        if (ScriptProcessRunner.UsesCommandPrompt(ScriptShells.Normalize(result.Shell)) && result.Command.Length > ScriptDefaults.MaximumCmdCommandLength)
            throw new ArgumentException(L("cmd commands are limited to 7900 characters. Use PowerShell for longer scripts.", "cmd-Befehle sind auf 7900 Zeichen begrenzt. Verwende PowerShell für längere Skripte."));
        if (result.IsEnabled && string.IsNullOrWhiteSpace(result.Command)) throw new ArgumentException("Enter a command before enabling the script.");
        return result;
    }

    /// <inheritdoc />
    public Task SaveProfileSettingsAsync(string profileId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        lock (_settingsLock)
        {
            ct.ThrowIfCancellationRequested();
            if (profileId == "none" && Selected is null && values.Count == 1 && values.TryGetValue("template", out var emptyTemplate))
            {
                _template = Template(emptyTemplate).Id;
                return Task.CompletedTask;
            }
            var draft = ReadDraft(profileId, values);
            ActiveService.UpdateScript(draft); // One validated, atomic write for every field.
            if (values.TryGetValue("template", out var template)) _template = Template(template).Id;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions
    {
        get
        {
            lock (_settingsLock)
            {
                var actions = new List<PluginSettingsAction>
                {
                    Action("add", L("Add script", "Skript hinzufügen"), L("Add a blank disabled script.", "Ein leeres, deaktiviertes Skript anlegen.")),
                    Action("add-template", L("Add selected template", "Ausgewählte Vorlage hinzufügen"), L("Create a disabled copy of the selected template.", "Eine deaktivierte Kopie der ausgewählten Vorlage anlegen."))
                };
                if (Selected is { } s)
                {
                    actions.Add(Action("test:" + s.Id, L("Run test", "Skript testen"), L("Run the entered command with sample text without saving or enabling it.", "Den eingegebenen Befehl mit Beispieltext ausführen, ohne ihn zu speichern oder zu aktivieren.")));
                    actions.Add(Action("remove:" + s.Id, L("Remove", "Skript entfernen…"), L("Remove this script.", "Dieses Skript entfernen.")));
                    if (ActiveService.Scripts.IndexOf(s) > 0) actions.Add(Action("up:" + s.Id, L("Move up", "Früher ausführen"), ""));
                    if (ActiveService.Scripts.IndexOf(s) < ActiveService.Scripts.Count - 1) actions.Add(Action("down:" + s.Id, L("Move down", "Später ausführen"), ""));
                }
                return actions;
            }
        }
    }
    private static PluginSettingsAction Action(string id, string title, string description) => new(id, title, description) { Section = PluginSettingsSection.Connection };
    private static ScriptTemplate Template(string id) => ScriptTemplates.All.SingleOrDefault(t => t.Id == id) ?? throw new ArgumentException("Unknown template.");

    /// <inheritdoc />
    public Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        lock (_settingsLock)
        {
            ct.ThrowIfCancellationRequested();
            if (id is "add" or "add-template")
            {
                var entry = id == "add" ? new ScriptEntry { Name = L("New script", "Neues Skript"), Shell = ScriptShells.WindowsPowerShell, IsEnabled = false } : Template(_template).Create(German);
                ActiveService.AddScript(entry); _editing = entry.Id;
            }
            else if (Selected is { } s && id == "remove:" + s.Id) { ActiveService.RemoveScript(s.Id); _editing = null; }
            else if (Selected is { } up && id == "up:" + up.Id) ActiveService.MoveUp(up.Id);
            else if (Selected is { } down && id == "down:" + down.Id) ActiveService.MoveDown(down.Id);
            else throw new ArgumentException("Unknown or stale action.");
            return Task.FromResult<string?>(L("Saved.", "Gespeichert."));
        }
    }

    /// <inheritdoc />
    public async Task<PluginProfileActionResult> ExecuteProfileActionAsync(string profileId, string actionId, IReadOnlyDictionary<string, string> values, string? apiKey, CancellationToken ct)
    {
        ScriptEntry? test = null;
        lock (_settingsLock)
        {
            ct.ThrowIfCancellationRequested();
            if (profileId != ConnectionIdentity) throw new ArgumentException("The selected script changed.");
            if (actionId == "add-template")
            {
                var template = Template(values.GetValueOrDefault("template", _template));
                var entry = template.Create(German);
                ActiveService.AddScript(entry); _editing = entry.Id; _template = template.Id;
                return new(L("Template added; enable it after reviewing the command.", "Vorlage hinzugefügt; nach Prüfung des Befehls aktivieren."));
            }
            if (Selected is { } selected && actionId == "test:" + selected.Id)
                test = ReadDraft(profileId, values);
        }
        if (test is null) return new((await ExecuteSettingsActionAsync(actionId, ct))!);
        if (string.IsNullOrWhiteSpace(test.Command)) throw new ArgumentException(L("Enter a command before testing.", "Gib vor dem Test einen Befehl ein."));
        var result = await ActiveService.Runner.RunAsync(test, "Hallo TypeWhisper!\nZweite Zeile: Äpfel & Öl.", new PostProcessingContext(), ct);
        if (!result.IsSuccess) return new(L("Test failed: ", "Test fehlgeschlagen: ") + result.Status);
        var preview = result.Output.Length > 2000 ? result.Output[..2000] + "…" : result.Output;
        return new(L("Result: ", "Ergebnis: ") + preview);
    }
}
