using System.Net.Http;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed class WinUIPluginPackages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    internal PortablePluginStore Store { get; } = new(WinUIProfile.DataPath("PluginPackages"), LocalCtcVocabulary.HostVersion, Http, CreateServices);
    internal PortablePluginCatalog Catalog { get; } = new(Http);
    internal Task InitializeAsync() => Task.Run(() => Store.InitializeAsync(Path.Combine(AppContext.BaseDirectory, "Plugins")));
    private static VocabularyHostServices CreateServices(string id)
    {
        var data = WinUIProfile.DataPath("PluginData", id);
        return new(data, secrets: new WindowsPluginSecretStore(data), assetDirectory: id == LocalTranscriptionPlugin.PluginId
            ? WinUIProfile.PluginAssetPath(id) : data);
    }
}
