using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.Soniox;
public sealed partial class SonioxPlugin : IApiKeyPlugin, IPluginTextSettings
{
    async Task IApiKeyPlugin.SetApiKeyAsync(string apiKey)
    {
        var previous = _apiKey;
        try { await SetApiKeyAsync(apiKey); }
        catch { _apiKey = previous; throw; }
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured || !await ValidateApiKeyAsync(_apiKey!, ct))
            throw new InvalidOperationException("The API key could not be validated.");
    }
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("region", "Region", "", _region) { Choices = AvailableRegions.Select(r => new PluginSettingChoice(r.Id,r.DisplayName)).ToArray() }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var setting = TextSettings.FirstOrDefault(s => s.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        value = value.Trim();
        if (value.Length > setting.MaxLength || value.Contains('\r') || value.Contains('\n') || setting.Choices.Count > 0 && !setting.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.", nameof(value));
        switch (id) { case "region": SetRegion(value); break; default: throw new ArgumentException("Unknown setting.", nameof(id)); }
        return Task.CompletedTask;
    }
}
