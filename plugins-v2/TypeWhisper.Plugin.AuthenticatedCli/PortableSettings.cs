using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.AuthenticatedCli;
public sealed partial class AuthenticatedCliPlugin : IPluginTextSettings, IPluginSettingsActions
{
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => CliProviderDescriptor.All.Select(d =>
        new PluginTextSetting(d.Key, GetString(d.DisplayKey), "Executable path; leave empty for automatic discovery.", _selectedExecutables.GetValueOrDefault(d.Key) ?? "")
        { Suggestions = _discovery.FindCandidates(d.ExecutableName).ToArray() }).ToArray();
    /// <inheritdoc />
    public async Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var descriptor = CliProviderDescriptor.All.SingleOrDefault(d => d.Key == id) ?? throw new ArgumentException("Unknown provider.", nameof(id));
        if (value.Length > 0 && !_discovery.FindCandidates(descriptor.ExecutableName).Contains(value, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select a discovered executable.", nameof(value));
        await SelectExecutableAsync(descriptor, string.IsNullOrWhiteSpace(value) ? null : value, ct);
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => [new("refresh", "Refresh CLI availability", "Checks installed executables and existing sign-in sessions without processing text.")];
    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id != "refresh") throw new ArgumentException("Unknown action.", nameof(id));
        await RefreshFromSettingsAsync(ct);
        return string.Join("\n", AdditionalLlmProviders.Select(p => p.ProviderName + ": " + (p.IsAvailable ? "Ready" : "Unavailable")));
    }
}
