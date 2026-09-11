using System.Globalization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAi;

public sealed partial class OpenAiPlugin
{
    private string _transcriptionContext = "";
    private string _liveDelay = "low";
    private static readonly string[] LiveDelays = ["minimal", "low", "medium", "high", "xhigh"];

    /// <inheritdoc />
    public bool SupportsLanguageHints => SelectedModelEntry?.LanguageFormat == TranscriptionLanguageFormat.Plural;
    /// <inheritdoc />
    public bool SupportsStreamingCompletion => true;
    /// <inheritdoc />
    public bool SupportsStreamingForPrompt(string? prompt) => SupportsStreaming &&
        (string.IsNullOrWhiteSpace(prompt) || SupportsDictionaryTerms);

    /// <inheritdoc />
    public bool ShowApiKeySettings => _authMode == OpenAiAuthMode.ApiKey;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => BuildTextSettings().Where(f => f.Id != ReasoningEffortSettingName || SupportsReasoningEffort(_selectedLlmModelId ?? "")).OrderBy(f => SectionFor(f.Id)).Select(f => f with { Section = SectionFor(f.Id), SaveChoiceOnChange = f.Id == AuthModeSettingName, Title = L(f.Title), Description = L(f.Description), Choices = f.Choices.Select(c => c with { Title = L(c.Title) }).ToArray() }).ToArray();

    private IReadOnlyList<PluginTextSetting> BuildTextSettings() =>
    [
        Choice(AuthModeSettingName, "Connection method", "API key for OpenAI billing, or your separate ChatGPT login. Dictation and speech always require an API key.", _authMode.ToStorageValue(),
            new("api-key", "OpenAI API key"), new("chatgpt", HasChatGptCredentials ? "ChatGPT (signed in)" : "ChatGPT (sign in below)")),
        Choice(SelectedLlmModelSettingName, "Text model", "Default model for text processing. Refresh the model list after changing accounts.", _selectedLlmModelId ?? "", SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray()),
        Choice(ReasoningEffortSettingName, "Reasoning effort", "Applied only to models that support reasoning.", EffectiveReasoningEffort(_selectedLlmModelId ?? "") ?? "medium", SupportedReasoningEfforts(_selectedLlmModelId ?? "").Select(v => new PluginSettingChoice(v, v)).ToArray()),
        Choice(TemperatureModeSettingName, "Temperature", "Custom temperature applies only to compatible chat models.", _temperatureMode, new(TemperatureModeProviderDefault, "Provider default"), new(TemperatureModeCustom, "Custom")),
        new(TemperatureValueSettingName, "Custom temperature", "Number between 0 and 2.", _temperatureValue.ToString(CultureInfo.InvariantCulture), 8),
        new("transcriptionContext", "Transcription context", "Optional background for GPT Transcribe and GPT Live Transcribe. Dictionary words are sent separately. This context is sent to OpenAI with your audio.", _transcriptionContext, 8000) { IsMultiline = true },
        Choice("liveDelay", "Live transcription delay", "Lower delay shows results sooner; higher delay gives the model more context.", _liveDelay, LiveDelays.Select(v => new PluginSettingChoice(v, v)).ToArray()),
        Choice(SelectedVoiceSettingName, "Speech voice", "OpenAI cloud voice used when this provider is selected for readback.", SelectedVoiceId!, AvailableVoices.Select(v => new PluginSettingChoice(v.Id, v.DisplayName)).ToArray()),
        new(TtsInstructionsSettingName, "Speech instructions", "Optional tone, pace or accent instructions sent to OpenAI.", _ttsInstructions, 4000) { IsMultiline = true }
    ];

    private static PluginTextSetting Choice(string id, string title, string description, string value, params PluginSettingChoice[] choices) =>
        new(id, title, description, value) { Choices = choices };

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = TextSettings.SingleOrDefault(f => f.Id == id) ?? throw new ArgumentException("Unknown setting.");
        if (value.Length > field.MaxLength || field.Choices.Count > 0 && !field.Choices.Any(c => c.Value == value))
            throw new ArgumentException("Invalid setting value.");
        switch (id)
        {
            case AuthModeSettingName: SetAuthMode(OpenAiAuthModeExtensions.Parse(value)); break;
            case SelectedLlmModelSettingName: SelectLlmModel(value); break;
            case ReasoningEffortSettingName: SetReasoningEffort(value); break;
            case TemperatureModeSettingName: SetTemperatureMode(value); break;
            case TemperatureValueSettingName:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) || !double.IsFinite(temperature) || temperature is < 0 or > 2)
                    throw new ArgumentException("Temperature must be between 0 and 2.");
                SetTemperatureValue(temperature); break;
            case SelectedVoiceSettingName: SelectVoice(value); break;
            case TtsInstructionsSettingName: SetTtsInstructions(value); break;
            case "transcriptionContext": _host?.SetSetting(id, value.Trim()); _transcriptionContext = value.Trim(); break;
            case "liveDelay": _host?.SetSetting(id, value); _liveDelay = value; break;
        }
        _host?.NotifyCapabilitiesChanged();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSettingsAction> SettingsActions => BuildSettingsActions()
        .Where(a => a.Id == "refresh" || _authMode == OpenAiAuthMode.ChatGpt && (a.Id != "logout" || HasChatGptCredentials))
        .Select(a => a with { Title = L(a.Title), Description = L(a.Description), Section = a.Id == "refresh" ? PluginSettingsSection.TextProcessing : PluginSettingsSection.Connection }).ToArray();

    private static PluginSettingsSection SectionFor(string id) => id switch
    {
        AuthModeSettingName => PluginSettingsSection.Connection,
        "transcriptionContext" or "liveDelay" => PluginSettingsSection.Transcription,
        SelectedVoiceSettingName or TtsInstructionsSettingName => PluginSettingsSection.Speech,
        _ => PluginSettingsSection.TextProcessing
    };

    private static IReadOnlyList<PluginSettingsAction> BuildSettingsActions() =>
    [
        new("login", "Sign in with ChatGPT", "Opens your browser. Sign in there to use ChatGPT for text processing."),
        new("import", "Import existing Codex login", "Explicitly copies the login from your local Codex auth file into this plugin's protected storage."),
        new("logout", "Sign out of ChatGPT", "Removes this plugin's ChatGPT credentials; your API key is retained."),
        new("refresh", "Refresh models", "Fetches available models for the selected text processing account.")
    ];

    /// <inheritdoc />
    public async Task<string?> ExecuteSettingsActionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (id)
        {
            case "login": await LoginWithChatGptInBrowserAsync(cancellationToken); return "Signed in with ChatGPT.";
            case "import": await ImportExistingLoginAsync(ct: cancellationToken); return "Existing ChatGPT login imported.";
            case "logout": await ClearChatGptLoginAsync(); return "Signed out of ChatGPT.";
            case "refresh":
                await RefreshAvailableLlmModelsAsync(cancellationToken);
                return _lastModelRefreshSucceeded
                    ? "Model list refreshed." : "Models could not be refreshed. The previous list has been retained. Check your account and connection.";
            default: throw new ArgumentException("Unknown settings action.");
        }
    }

    /// <inheritdoc />
    public async Task ValidateConfigurationAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Store an OpenAI API key first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new("Bearer", _apiKey);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI API key verification failed (HTTP {(int)response.StatusCode}).");
    }

    private static readonly Lazy<IReadOnlyDictionary<string, string>> GermanStrings = new(() =>
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(OpenAiPlugin).Assembly.Location)!, "Localization", "de.json");
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, System.Text.Encoding.UTF8)) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return new Dictionary<string, string>(); }
    });
    private static string L(string text) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de"
        && GermanStrings.Value.TryGetValue(text, out var translated) ? translated : text;

    internal static string[] DictionaryKeywords(string? prompt) => string.IsNullOrWhiteSpace(prompt) ? [] :
        prompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
}
