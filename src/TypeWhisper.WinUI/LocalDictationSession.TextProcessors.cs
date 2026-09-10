using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private IReadOnlyList<PortablePostProcessor> _processorsAtStart = [];

    private IReadOnlyList<DictationTextProcessor> BindTextProcessors(IReadOnlyList<PortablePostProcessor> snapshots,
        string? language, string? app, string? profile, double duration)
    {
        var context = new PostProcessingContext { SourceLanguage = language, ActiveAppName = app,
            ActiveAppProcessName = app, ProfileName = profile, AudioDurationSeconds = duration };
        return snapshots.Select(snapshot => new DictationTextProcessor(snapshot.PluginId, snapshot.Version,
            snapshot.Priority, (text, ct) => PluginRuntime.ProcessTextAsync(snapshot, text, context, ct))).ToArray();
    }

    internal Task<string?> SavePluginTextSettingAsync(string id, string key, string value) =>
        ChangeRegistryPluginAsync(id, async () =>
        {
            await PluginRuntime.UseConfigurationAsync(id, async (plugin, ct) =>
            {
                if (plugin is not IPluginTextSettings settings) throw new NotSupportedException("Text settings are unavailable.");
                await settings.SaveTextSettingAsync(key, value, ct);
                return true;
            });
        });
}
