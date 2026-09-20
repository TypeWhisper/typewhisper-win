using TypeWhisper.PluginSDK;
namespace TypeWhisper.Plugin.GemmaLocal;
public sealed partial class GemmaLocalPlugin : IPluginTextSettings
{
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
