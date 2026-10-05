using TypeWhisper.PluginHost;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.WinUI;

internal static partial class SupportDiagnosticsExporter
{
    // Snapshot local state on the UI thread before collecting package metadata in the background.
    internal static IReadOnlyDictionary<string, (bool Enabled, bool HasError)> CaptureLocalPluginStates(
        LocalTranscriptionPlugin models, LocalCtcVocabulary vocabulary, string? localPluginError) =>
        new Dictionary<string, (bool Enabled, bool HasError)>
        {
            // CTC belongs to the Sherpa package and has no independent inventory entry.
            [LocalTranscriptionPlugin.PluginId] = (models.Enabled,
                localPluginError is not null || models.Error is not null || vocabulary.Error is not null)
        };

    internal static Report.PluginInfo[] Plugins(PortablePluginStore store, PortablePluginRuntimeRegistry runtime,
        IReadOnlyDictionary<string, (bool Enabled, bool HasError)> localStates)
    {
        if (!store.Initialized) throw new InvalidOperationException();
        var states = runtime.Snapshot().ToDictionary(state => state.PluginId, StringComparer.Ordinal);
        var transcription = runtime.TranscriptionProviders;
        var llm = runtime.LlmProviders;
        var tts = runtime.TtsProviders;
        return store.Inventory().Select(package =>
        {
            var manifest = package.Manifest;
            var id = manifest?.Id ?? Path.GetFileName(package.Directory);
            states.TryGetValue(id, out var state);
            var local = localStates.GetValueOrDefault(id);
            var providers = transcription.Where(provider => provider.PluginId == id)
                .Select(provider => new Report.ProviderInfo("transcription", Report.Identifier(provider.SelectionId),
                    provider.Ready, Report.Identifier(provider.SelectedModelId)))
                .Concat(llm.Where(provider => provider.PluginId == id).Select(provider =>
                    new Report.ProviderInfo("llm", Report.Identifier(provider.SelectionId), provider.Ready, null)))
                .Concat(tts.Where(provider => provider.PluginId == id).Select(provider =>
                    new Report.ProviderInfo("tts", Report.Identifier(provider.PluginId), provider.Ready, null))).ToArray();
            return new Report.PluginInfo(Report.Identifier(id), Report.Identifier(manifest?.Version), manifest?.IsLocal,
                state?.Enabled ?? local.Enabled, package.Error is not null || state?.Error is not null || local.HasError,
                store.PendingRestart(id), store.UpdateWarning(id) is not null, providers);
        }).OrderBy(plugin => plugin.Id, StringComparer.Ordinal).ToArray();
    }
}
