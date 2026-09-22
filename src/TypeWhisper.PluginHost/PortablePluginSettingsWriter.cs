using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>Outcome of one explicit save, including writes completed before a provider failure.</summary>
public sealed record PortablePluginSettingsSaveResult(IReadOnlyList<string> SavedFields, bool ApiKeySaved, string? Error);

/// <summary>Combines host-rendered edits under the caller's configuration lease without claiming a cross-field transaction.</summary>
public static class PortablePluginSettingsWriter
{
    /// <summary>Validates known field constraints before writing changed values; a blank key keeps the stored credential.</summary>
    public static async Task<PortablePluginSettingsSaveResult> SaveAsync(ITypeWhisperPlugin plugin,
        IReadOnlyDictionary<string, string> changes, string? apiKey, CancellationToken ct)
    {
        var settings = plugin as IPluginTextSettings;
        var fields = settings?.TextSettings.ToArray() ?? [];
        foreach (var (id, value) in changes)
        {
            var field = fields.FirstOrDefault(f => f.Id == id);
            if (field is null) return new([], false, "The available settings changed. Reopen this page before saving.");
            if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
                return new([], false, "Check the value for “" + field.Title + "” before saving.");
        }
        if (!string.IsNullOrWhiteSpace(apiKey) && plugin is not IApiKeyPlugin)
            return new([], false, "This plugin does not accept an API key.");

        var saved = new List<string>();
        string current = "settings";
        try
        {
            foreach (var field in fields)
            {
                if (!changes.TryGetValue(field.Id, out var value) || value == field.Value) continue;
                ct.ThrowIfCancellationRequested();
                current = field.Title;
                await settings!.SaveTextSettingAsync(field.Id, value, ct);
                saved.Add(field.Id);
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                ct.ThrowIfCancellationRequested();
                current = "API key";
                await ((IApiKeyPlugin)plugin).SetApiKeyAsync(apiKey);
                return new(saved, true, null);
            }
            return new(saved, false, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var prefix = saved.Count == 0 ? "Could not save “" : "Some changes were saved. Could not save “";
            return new(saved, false, prefix + current + "”. Your remaining edits are kept; check the value and retry.");
        }
    }
}
