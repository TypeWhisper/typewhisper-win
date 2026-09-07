using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.PortableFixture;

/// <summary>A portable multi-capability package used only by runtime ownership tests.</summary>
public class RuntimeProbePlugin : ITranscriptionEnginePlugin, ILlmProviderPlugin, IApiKeyPlugin,
    ITranscriptionEngineSelectionIdentity, IAdditionalTranscriptionEnginesProvider, IAdditionalLlmProvidersProvider, IPostProcessorPlugin, IActionPlugin
{
    private IPluginHostServices? _host;
    private bool _active;
    private readonly ExtraRole _extra;
    /// <summary>Creates the fixture's additional roles without activating them separately.</summary>
    public RuntimeProbePlugin() => _extra = new(this);
    /// <inheritdoc />
    public virtual string PluginId => "test.typewhisper.runtime";
    /// <inheritdoc />
    public string PluginName => "Runtime fixture";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string ProviderId => PluginId;
    /// <inheritdoc />
    public string ProviderDisplayName => "Fixture transcription";
    /// <inheritdoc />
    public string ProviderName => "Fixture LLM";
    /// <inheritdoc />
    public string TranscriptionSelectionId => _host?.GetSetting<string>("SharedSelection") ?? PluginId;
    /// <inheritdoc />
    public bool IsConfigured => _active;
    /// <inheritdoc />
    public bool IsAvailable => _active;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("transcription", "Fixture transcription")];
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> SupportedModels => [new("llm", "Fixture LLM")];
    /// <inheritdoc />
    public string? SelectedModelId => "transcription";
    /// <inheritdoc />
    public bool SupportsTranslation => false;
    /// <inheritdoc />
    public IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines => _host?.GetSetting<bool>("ExtraRole") == true ? [_extra] : [];
    /// <inheritdoc />
    public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders => _host?.GetSetting<bool>("ExtraRole") == true ? [_extra] : [];
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host; _active = true;
        host.SetSetting("activations", host.GetSetting<int>("activations") + 1);
        host.NotifyCapabilitiesChanged(); // Synchronous activation notification must not deadlock the owner.
        return Task.CompletedTask;
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
    public void SelectModel(string modelId) => _host!.NotifyCapabilitiesChanged();
    /// <inheritdoc />
    public async Task<PluginTranscriptionResult> TranscribeAsync(byte[] audio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        var text = await ProcessAsync("", "transcribed", "llm", ct);
        return new PluginTranscriptionResult(text, language, 1, null);
    }
    /// <inheritdoc />
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        if (!_active) throw new InvalidOperationException("Fixture is inactive.");
        _host!.Log(PluginLogLevel.Info, "request-start");
        if (_host.GetSetting<bool>("Hold")) await _host.LoadSecretAsync("hold"); // Deliberately ignores cancellation until drained.
        _host.NotifyCapabilitiesChanged();
        return userText;
    }
    /// <inheritdoc />
    public string ProcessorName => "Fixture text processor";
    /// <inheritdoc />
    public int Priority => 250;
    /// <inheritdoc />
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
        => ProcessAsync("", text, "llm", ct);

    /// <inheritdoc />
    public string ActionId => "write-fixture";
    /// <inheritdoc />
    public string ActionName => "Write fixture";
    /// <inheritdoc />
    public string? ActionIcon => null;
    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    {
        await ProcessAsync("", input, "llm", ct);
        _host!.SetSetting("actionWrites", _host.GetSetting<int>("actionWrites") + 1);
        if (_host.GetSetting<bool>("ActionThrows")) throw new IOException("private action failure");
        return new(true, "Committed fixture", "https://must-not-open.invalid/");
    }

    /// <inheritdoc />
    public Task SetApiKeyAsync(string apiKey)
    {
        _host!.SetSetting("ExtraRole", apiKey == "extra");
        _host.SetSetting("Collision", apiKey == "collision");
        if (apiKey == "collision") _host.SetSetting("ExtraRole", true);
        _host.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }
    /// <inheritdoc />
    public Task ValidateConfigurationAsync(CancellationToken ct) => Task.CompletedTask;

    private sealed class ExtraRole(RuntimeProbePlugin owner) : ITranscriptionEnginePlugin, ILlmProviderPlugin,
        ITranscriptionEngineSelectionIdentity, ILlmProviderSelectionIdentity
    {
        public string PluginId => owner.PluginId;
        public string PluginName => "Additional fixture role";
        public string PluginVersion => owner.PluginVersion;
        public string ProviderId => "extra";
        public string ProviderDisplayName => PluginName;
        public string ProviderName => PluginName;
        public string TranscriptionSelectionId => owner._host?.GetSetting<bool>("Collision") == true ? owner.TranscriptionSelectionId : owner.PluginId + "/extra";
        public string LlmSelectionId => owner.PluginId + "/extra";
        public bool IsConfigured => owner.IsConfigured;
        public bool IsAvailable => owner.IsAvailable;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => owner.TranscriptionModels;
        public IReadOnlyList<PluginModelInfo> SupportedModels => owner.SupportedModels;
        public string? SelectedModelId => owner.SelectedModelId;
        public bool SupportsTranslation => false;
        public Task ActivateAsync(IPluginHostServices host) => throw new InvalidOperationException("Additional roles must not be activated separately.");
        public Task DeactivateAsync() => throw new InvalidOperationException("Additional roles must not be deactivated separately.");
        public void Dispose() => throw new InvalidOperationException("Additional roles must not be disposed separately.");
        public void SelectModel(string modelId) => owner.SelectModel(modelId);
        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] audio, string? language, bool translate, string? prompt, CancellationToken ct) => owner.TranscribeAsync(audio, language, translate, prompt, ct);
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) => owner.ProcessAsync(systemPrompt, userText, model, ct);
    }
}

/// <summary>A second package identity for collision tests using the same fixture binary.</summary>
public sealed class OtherRuntimeProbePlugin : RuntimeProbePlugin
{
    /// <inheritdoc />
    public override string PluginId => "test.typewhisper.runtime-other";
}
