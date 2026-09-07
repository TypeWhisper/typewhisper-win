using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>A network-free Groq-identity fixture for the WinUI adapter's registry path.</summary>
public sealed class CloudRegistryProbePlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, IApiKeyPlugin
{
    private IPluginHostServices? _host;
    private string? _key;
    private string _model = "first";
    private bool _active;
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.groq";
    /// <inheritdoc />
    public string PluginName => "Cloud registry fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => "groq";
    /// <inheritdoc />
    public string ProviderDisplayName => "Groq fixture";
    /// <inheritdoc />
    public string ProviderName => "Groq fixture LLM";
    /// <inheritdoc />
    public bool IsConfigured => _active && !string.IsNullOrWhiteSpace(_key);
    /// <inheritdoc />
    public bool IsAvailable => IsConfigured;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("first", "First model"), new("second", "Second model")];
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => [new("llm", "Text model")];
    /// <inheritdoc />
    public string? SelectedModelId => _model;
    /// <inheritdoc />
    public bool SupportsTranslation => _model == "second";
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedLanguages => ["en", "de"];
    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _key = await host.LoadSecretAsync("api-key");
        _model = host.GetSetting<string>("selectedModel") ?? "first";
        _active = true;
        host.SetSetting("activations", host.GetSetting<int>("activations") + 1);
        host.NotifyCapabilitiesChanged();
    }
    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        _active = false;
        _host!.SetSetting("deactivations", _host.GetSetting<int>("deactivations") + 1);
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public void Dispose() => _host?.SetSetting("disposals", _host.GetSetting<int>("disposals") + 1);
    /// <inheritdoc />
    public async Task SetApiKeyAsync(string apiKey)
    {
        var trimmed = apiKey.Trim();
        if (trimmed.Length == 0) await _host!.DeleteSecretAsync("api-key");
        else await _host!.StoreSecretAsync("api-key", trimmed);
        _key = trimmed;
        _host.NotifyCapabilitiesChanged();
    }
    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new InvalidOperationException("No fixture key.");
        _host!.SetSetting("validations", _host.GetSetting<int>("validations") + 1);
    }
    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        if (!TranscriptionModels.Any(model => model.Id == modelId)) throw new ArgumentException("Unknown fixture model.");
        _host!.SetSetting("selectedModel", modelId);
        _model = modelId;
        _host.NotifyCapabilitiesChanged();
    }
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("No fixture key.");
        _host!.SetSetting("lastLanguage", language);
        _host.SetSetting("lastModel", _model);
        _host.SetSetting("lastTranslate", translate);
        _host.SetSetting("wavLength", wavAudio.Length);
        _host.Log(PluginLogLevel.Info, "decode-start");
        if (_host.GetSetting<bool>("HoldDecode")) await _host.LoadSecretAsync("pause");
        await Task.Yield(); // Exercise a continuation on the caller's synchronization context.
        _host.NotifyCapabilitiesChanged();
        return new("fixture transcript", language, 1, null);
    }
    /// <inheritdoc />
    public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException("No fixture key.");
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(userText);
    }
}
