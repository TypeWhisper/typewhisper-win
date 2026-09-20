using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        Field("model", "Transcription model", "Transkriptionsmodell", SelectedModelId ?? "", PluginSettingsSection.Transcription,
            TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray()) with
            {
                Value = SelectedModelId ?? "",
                Description = IsRealtime ? Connection.L("Live transcription; the language is detected automatically.",
                    "Live-Transkription; die Sprache wird automatisch erkannt.") : Connection.L("Transcribes after recording stops.", "Transkribiert nach dem Ende der Aufnahme.")
            },
        Field("llmModel", "Text model", "Textmodell", SelectedTextModel, PluginSettingsSection.TextProcessing,
            SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray()) with { Value = SelectedTextModel },
        Field("temperatureMode", "Temperature", "Temperatur", "providerDefault", PluginSettingsSection.TextProcessing,
            new("providerDefault", Connection.L("Provider default", "Anbieterstandard")), new("custom", Connection.L("Custom", "Benutzerdefiniert"))),
        Field("temperature", "Custom temperature (0–2)", "Eigene Temperatur (0–2)", "0.3", PluginSettingsSection.TextProcessing)
            with { VisibleWhen = new("temperatureMode", ["custom"]) }
    ];

    /// <inheritdoc />
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        model = string.IsNullOrWhiteSpace(model) ? SelectedTextModel : model.Trim();
        if (!SupportedModels.Any(m => m.Id == model))
            throw new PluginRequestException("Unknown Mistral text model. Refresh models and select a text model.", PluginRequestFailureKind.Configuration);
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userText } },
            ["max_tokens"] = LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
        };
        if (Connection.Get("temperatureMode", "providerDefault") == "custom")
            body["temperature"] = double.Parse(Connection.Get("temperature", "0.3"), CultureInfo.InvariantCulture);
        using var request = Connection.Request(HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions");
        request.Content = ProviderConnection.Json(body);
        using var document = await Connection.ReadAsync(request, ct);
        var root = document.RootElement;
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Mistral");
        var choices = ProviderConnection.Required(root, "choices", JsonValueKind.Array);
        if (choices.GetArrayLength() == 0 || ProviderConnection.Text(choices[0], "finish_reason") != "stop")
            throw ProviderConnection.InvalidResponse();
        var message = ProviderConnection.Required(choices[0], "message", JsonValueKind.Object);
        if (!message.TryGetProperty("content", out var content)) throw ProviderConnection.InvalidResponse();
        string? result = null;
        if (content.ValueKind == JsonValueKind.String) result = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            // Reasoning models may return thinking chunks. Only final text belongs in the user's field.
            var text = new StringBuilder();
            foreach (var chunk in content.EnumerateArray())
                if (ProviderConnection.Text(chunk, "type") == "text") text.Append(ProviderConnection.Text(chunk, "text"));
            result = text.ToString();
        }
        return !string.IsNullOrWhiteSpace(result) ? result.Trim() : throw ProviderConnection.InvalidResponse();
    }
}
