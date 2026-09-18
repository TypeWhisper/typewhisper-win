using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
namespace TypeWhisper.Plugin.FileMemory;
public sealed partial class FileMemoryPlugin : IActionPlugin, IPluginTextSettings, IPluginSettingsActions
{
    /// <inheritdoc />
    public string? ActionIcon => "brain";
    private string _entry = "";
    private string _query = "";
    /// <inheritdoc />
    public string ActionId => "store-" + PluginId;
    /// <inheritdoc />
    public string ActionName => "Remember in " + PluginName;
    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    { await StoreAsync(input, ct); return new(true, "Memory stored."); }
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [
        new("entry", "Memory text", "Used by Store and Delete exact entry. Editor text is kept only for this session.", _entry) { IsMultiline = true },
        new("query", "Search", "Find relevant saved memories.", _query)];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.Length > 32768) throw new ArgumentException("Text is too long.", nameof(value));
        switch (id) { case "entry": _entry = value; break; case "query": _query = value; break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [
        new("store", "Store entry", "Stores the memory text."), new("search", "Search memories", "Shows up to ten matching entries."),
        new("list", "List memories", "Shows up to fifty saved entries."), new("delete", "Delete exact entry", "Deletes only the entry exactly matching Memory text.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (id)
        {
            case "store": await StoreAsync(_entry, ct); return "Stored.";
            case "search": return string.Join("\n\n", await SearchAsync(_query, 10, ct));
            case "list": return string.Join("\n\n", (await GetAllAsync(ct)).Take(50));
            case "delete": await DeleteAsync(_entry, ct); return "Deleted matching entry.";
            default: throw new ArgumentException("Unknown action.", nameof(id));
        }
    }
}
