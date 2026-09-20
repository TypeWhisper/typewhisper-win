using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.GemmaLocal;
public sealed partial class GemmaLocalPlugin : IPluginTextSettings, IPluginSettingsActions
{
    private string L(string en, string de) => Loc?.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en;

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => _host is null ? [] :
        [new("discard-partial-downloads", L("Discard incomplete downloads", "Unvollständige Downloads verwerfen"),
            L("Interrupted downloads resume on the next Download click. This removes their saved data; completed models are kept.",
              "Unterbrochene Downloads werden beim nächsten Klick auf Download fortgesetzt. Diese Aktion entfernt ihre gespeicherten Daten; vollständige Modelle bleiben erhalten."))];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken ct)
    {
        if (id != "discard-partial-downloads") throw new ArgumentException("Unknown action.", nameof(id));
        if (_host is null) throw new InvalidOperationException("Activate the plugin first.");
        await BeginModelMutationAsync(ct);
        try
        {
            foreach (var model in Models)
            {
                ct.ThrowIfCancellationRequested();
                var path = GetModelFilePath(model.Id, model.FileName) + ".download";
                File.Delete(path);
                File.Delete(path + ".tmp");
            }
        }
        finally { _inferenceLock.Release(); StartCacheVerification(); _host?.NotifyCapabilitiesChanged(); }
        return L("Incomplete downloads removed.", "Unvollständige Downloads entfernt.");
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => [new("threads", "CPU threads", "Applied the next time a model is loaded. Automatic uses half the available processors.", (_host?.GetSetting<int?>("threads") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture))
        { Choices = new[] { 0, 2, 4, 8, 16 }.Select(n => new PluginSettingChoice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == 0 ? "Automatic" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray() }];
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == "threads")
        {
            if (!int.TryParse(value, out var threads) || !new[] { 0, 2, 4, 8, 16 }.Contains(threads)) throw new ArgumentException("Choose a supported thread count.");
            (_host ?? throw new InvalidOperationException("Activate the plugin first.")).SetSetting("threads", threads);
            return Task.CompletedTask;
        }
        if (id != "model") throw new ArgumentException("Unknown setting.", nameof(id));
        SelectModel(value);
        return Task.CompletedTask;
    }
}
