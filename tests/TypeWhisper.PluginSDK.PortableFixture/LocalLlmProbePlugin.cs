using TypeWhisper.PluginSDK.Models;
namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>Local text model fixture that counts loads and unloads in its settings.</summary>
public sealed class LocalLlmProbePlugin : ILlmProviderPlugin, ILocalLlmModelManagement
{
    private static readonly PluginModelInfo[] Catalog = [new("small", "Small fixture model"), new("large", "Large fixture model")];
    private IPluginHostServices _host = null!;
    private string? _loaded;
    /// <inheritdoc />
    public string PluginId => "test.typewhisper.runtime";
    /// <inheritdoc />
    public string PluginName => "Local text fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderName => PluginName;
    /// <inheritdoc />
    public bool IsAvailable => _loaded is not null;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => Catalog.Where(model => model.Id == _loaded).ToArray();
    /// <inheritdoc />
    public IReadOnlyList<LocalLlmModelState> LocalModels => Catalog.Select(model => new LocalLlmModelState(model, true, model.Id == _loaded)).ToArray();
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) { _host = host; return Task.CompletedTask; }
    /// <inheritdoc />
    public Task DeactivateAsync() { _loaded = null; return Task.CompletedTask; }
    /// <inheritdoc />
    public void Dispose() { }
    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
        model == _loaded ? Task.FromResult(model + ":" + userText) : throw new InvalidOperationException("Load the model first.");
    /// <inheritdoc />
    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
    /// <inheritdoc />
    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        if (_host.GetSetting<bool>("FailLoad")) throw new IOException("Fixture load failure.");
        _loaded = modelId;
        _host.SetSetting("loads", _host.GetSetting<int>("loads") + 1);
        _host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task UnloadModelAsync(CancellationToken ct)
    {
        _loaded = null;
        _host.SetSetting("unloads", _host.GetSetting<int>("unloads") + 1);
        _host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task RemoveModelAsync(string modelId, CancellationToken ct)
    {
        if (_loaded == modelId) _loaded = null;
        return Task.CompletedTask;
    }
}
