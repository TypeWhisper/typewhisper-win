using System.Net.Http;
using TypeWhisper.PluginHost;

namespace TypeWhisper.WinUI;

internal sealed class WinUIPluginPackages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    internal PortablePluginStore Store { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TypeWhisper-WinUI-DevUserData", "PluginPackages"), LocalCtcVocabulary.HostVersion, Http, CreateServices);
    internal PortablePluginCatalog Catalog { get; } = new(Http);
    internal Task InitializeAsync() => Task.Run(() => Store.InitializeAsync(Path.Combine(AppContext.BaseDirectory, "Plugins")));
    private static VocabularyHostServices CreateServices(string id)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(local, "TypeWhisper-WinUI-DevUserData", "PluginData", id);
        return new(data, secrets: new WindowsPluginSecretStore(data), assetDirectory: id == LocalTranscriptionPlugin.PluginId
            ? Path.Combine(local, "TypeWhisper-DevUserData", "PluginData", id) : data);
    }
}
