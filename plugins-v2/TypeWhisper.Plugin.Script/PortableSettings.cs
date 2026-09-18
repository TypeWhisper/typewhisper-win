using System.Globalization;
using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Script;
public sealed partial class ScriptPlugin : IPluginTextSettings, IPluginSettingsActions
{
    private ScriptService ActiveService => Service ?? throw new InvalidOperationException("Plugin is not active.");
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => ActiveService.Scripts.SelectMany(s => new PluginTextSetting[] {
        new(s.Id + ":name", "Name", "", s.Name, 200),
        new(s.Id + ":command", "Command", "Transcription text is passed on stdin. Only enable commands you trust.", s.Command) { IsMultiline = true },
        new(s.Id + ":shell", "Shell", "", s.Shell) { Choices = ScriptShells.Supported.Select(x => new PluginSettingChoice(x,x)).ToArray() },
        new(s.Id + ":timeout", "Timeout in seconds", "1–300", s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), 3),
        new(s.Id + ":enabled", "Enabled", "Runs after each transcription when the plugin is enabled.", s.IsEnabled.ToString().ToLowerInvariant()) { Choices = [new("false","Off"),new("true","On")] }
    }).ToArray();
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parts = id.Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var key)) throw new ArgumentException("Unknown setting.", nameof(id));
        var current = ActiveService.Scripts.Single(s => s.Id == key);
        var field = TextSettings.Single(s => s.Id == id);
        if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value)) throw new ArgumentException("Invalid value.", nameof(value));
        var next = parts[1] switch {
            "name" => current with { Name = value.Trim() },
            "command" => current with { Command = value },
            "shell" => current with { Shell = value },
            "enabled" => current with { IsEnabled = bool.Parse(value) },
            "timeout" when int.TryParse(value, out var seconds) && seconds is >= 1 and <= 300 => current with { TimeoutSeconds = seconds },
            _ => throw new ArgumentException("Invalid setting.", nameof(id))
        };
        ActiveService.UpdateScript(next);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("add", "Add script", "Adds a disabled script to the end of the chain."),
        .. ActiveService.Scripts.Select(s => new PluginSettingsAction("remove:" + s.Id, "Remove " + s.Name, "Removes this command from the chain.")),
        .. ActiveService.Scripts.Select(s => new PluginSettingsAction("up:" + s.Id, "Move up: " + s.Name, "Runs this script earlier."))];
    /// <inheritdoc />
    public Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "add") ActiveService.AddScript(new ScriptEntry { Name = "New script", IsEnabled = false });
        else if (id.StartsWith("remove:") && Guid.TryParse(id[7..], out var remove)) ActiveService.RemoveScript(remove);
        else if (id.StartsWith("up:") && Guid.TryParse(id[3..], out var up)) ActiveService.MoveUp(up);
        else throw new ArgumentException("Unknown action.", nameof(id));
        return Task.FromResult<string?>("Saved.");
    }
}
