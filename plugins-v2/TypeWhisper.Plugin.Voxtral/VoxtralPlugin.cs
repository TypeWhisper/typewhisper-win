using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>Independent portable Voxtral (Mistral API) provider, compared with Windows 4db8f6ac and macOS ac00e39e.</summary>
public sealed partial class VoxtralPlugin : ITranscriptionEnginePlugin, IApiKeyPlugin, IPluginTextSettings, ILlmProviderPlugin, ILlmRequestHedgingSupport, IPluginSettingsActions
{
    private readonly ProviderConnection Connection;
    /// <summary>Creates a provider with isolated HTTP transport.</summary>
    public VoxtralPlugin() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) }) { }
    internal VoxtralPlugin(HttpClient http) => Connection = new(http);
    /// <inheritdoc />
    public string PluginId => "com.typewhisper.voxtral";
    /// <inheritdoc />
    public string PluginName => "Mistral";
    /// <inheritdoc />
    public string PluginVersion => "1.3.0";
    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host) => Connection.ActivateAsync(host);
    /// <inheritdoc />
    public Task DeactivateAsync() { Connection.Deactivate(); return Task.CompletedTask; }
    /// <inheritdoc />
    public bool IsConfigured => Connection.Configured;
    /// <inheritdoc />
    public Task SetApiKeyAsync(string apiKey) => Connection.SetKeyAsync(apiKey);
    /// <inheritdoc />
    public void Dispose() => Connection.Dispose();

    private PluginTextSetting Field(string id, string en, string de, string fallback, PluginSettingsSection section,
        params PluginSettingChoice[] choices) => new(id, Connection.L(en, de), "", Connection.Get(id, fallback)) { Section = section, Choices = choices };
    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = TextSettings.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown setting.");
        value = value.Trim();
        if (id == "model" && !TranscriptionModels.Any(m => m.Id == value)
            || id == "llmModel" && !SupportedModels.Any(m => m.Id == value))
            throw new ArgumentException("Unknown model. Refresh models and select an available model.");
        if (value.Length > 2048 || value.Any(char.IsControl) || (field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value)))
            throw new ArgumentException("Invalid setting value.");
        ValidateValue(id, value);
        return Connection.SaveAsync(id, value, cancellationToken);
    }
    private static void ValidateValue(string id, string value)
    {
        if (id == "temperature" && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < 0 || number > 2))
            throw new ArgumentException("Temperature must be a number from 0 to 2.");
        if (id == "accountId" && value.Length != 0 && (value.Length != 32 || !value.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Account ID must contain 32 hexadecimal characters.");
        if (id is "teamId" or "projectId" && value.Length != 0 && !Guid.TryParse(value, out _))
            throw new ArgumentException("Enter a valid UUID.");
        if (id == "baseUrl") _ = Endpoint(value);
        if (id is "llmModel" or "apiModel" && (value.Length == 0 || value.Any(char.IsWhiteSpace))) throw new ArgumentException("Enter a model ID.");
    }
    private static string Endpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTPS URL, or HTTP on localhost, without credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    /// <inheritdoc />
    public string ProviderId => "voxtral";
    /// <inheritdoc />
    public string ProviderDisplayName => PluginName;
    /// <inheritdoc />
    public IReadOnlyList<PluginModelInfo> TranscriptionModels => Catalog.Transcription;
    /// <inheritdoc />
    public string? SelectedModelId => TranscriptionModels.Any(m => m.Id == Connection.Get("model")) ? Connection.Get("model")
        : TranscriptionModels.FirstOrDefault(m => m.Id == "voxtral-mini-latest")?.Id ?? TranscriptionModels.FirstOrDefault()?.Id;
    /// <inheritdoc />
    public void SelectModel(string modelId)
    {
        if (!TranscriptionModels.Any(m => m.Id == modelId)) throw new ArgumentException("Unknown transcription model.");
        Connection.SaveAsync("model", modelId, default).GetAwaiter().GetResult();
    }
}
