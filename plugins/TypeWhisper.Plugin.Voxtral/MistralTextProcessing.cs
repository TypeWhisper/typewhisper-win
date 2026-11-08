using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin
{
    private const string ReasoningSettingPrefix = "reasoningEffort.";

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings =>
    [
        Field("model", "Transcription model", "Transkriptionsmodell", SelectedModelId ?? "", PluginSettingsSection.Transcription,
            TranscriptionModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray()) with
            {
                Value = SelectedModelId ?? "",
                Description = !IsRealtime ? Connection.L("Transcribes after recording stops.", "Transkribiert nach dem Ende der Aufnahme.")
                    : BatchModel is { } batch
                        ? Connection.L($"Shows live text while you speak. The final text and files come from {MistralCatalog.DisplayName(batch.Id, batch.Name)}.",
                            $"Zeigt Live-Text beim Sprechen. Den fertigen Text und Dateien liefert {MistralCatalog.DisplayName(batch.Id, batch.Name)}.")
                        : Connection.L("Shows live text while you speak and keeps it as the final text.",
                            "Zeigt Live-Text beim Sprechen und übernimmt ihn als fertigen Text.")
            },
        Field("llmModel", "Text model", "Textmodell", SelectedTextModel, PluginSettingsSection.TextProcessing,
            SupportedModels.Select(m => new PluginSettingChoice(m.Id, m.DisplayName)).ToArray()) with { Value = SelectedTextModel },
        .. Catalog.Text.Where(m => MistralCatalog.ReasoningEfforts(m.Id).Length > 0).Select(ReasoningField),
        Field("temperatureMode", "Temperature", "Temperatur", "providerDefault", PluginSettingsSection.TextProcessing,
            new("providerDefault", Connection.L("Provider default", "Anbieterstandard")), new("custom", Connection.L("Custom", "Benutzerdefiniert"))),
        Field("temperature", "Custom temperature (0–2)", "Eigene Temperatur (0–2)", "0.3", PluginSettingsSection.TextProcessing)
            with { VisibleWhen = new("temperatureMode", ["custom"]) }
    ];

    // One field per reasoning model, shown while that model is selected, because each model accepts different levels.
    private PluginTextSetting ReasoningField(MistralTextModel model)
    {
        var efforts = MistralCatalog.ReasoningEfforts(model.Id);
        return Field(ReasoningSettingPrefix + model.Id, "Reasoning effort", "Denkaufwand", ReasoningEffort(model.Id) ?? efforts[0],
            PluginSettingsSection.TextProcessing, efforts.Select((effort, index) => new PluginSettingChoice(effort, EffortTitle(effort, index == 0))).ToArray()) with
        {
            Value = ReasoningEffort(model.Id) ?? efforts[0],
            Description = Connection.L("Higher levels think longer before answering and take more time. Also applies when a workflow uses this model.",
                "Höhere Stufen denken vor der Antwort länger nach und brauchen mehr Zeit. Gilt auch, wenn ein Workflow dieses Modell nutzt."),
            VisibleWhen = new("llmModel", [model.Id])
        };
    }

    private string EffortTitle(string effort, bool fastest)
    {
        var title = effort switch
        {
            "none" => Connection.L("Off", "Aus"),
            "low" => Connection.L("Low", "Niedrig"),
            "medium" => Connection.L("Medium", "Mittel"),
            "high" => Connection.L("High", "Hoch"),
            _ => effort
        };
        return fastest ? title + Connection.L(" (fastest)", " (am schnellsten)") : title;
    }

    /// <summary>The saved level when the model accepts it, otherwise its fastest level; null for models without levels.</summary>
    private string? ReasoningEffort(string modelId)
    {
        var efforts = MistralCatalog.ReasoningEfforts(modelId);
        if (efforts.Length == 0) return null;
        var saved = Connection.Get(ReasoningSettingPrefix + modelId);
        return efforts.Contains(saved, StringComparer.Ordinal) ? saved : efforts[0];
    }

    /// <inheritdoc />
    public async Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requested = string.IsNullOrWhiteSpace(model) ? SelectedTextModel : model.Trim();
        // Workflows may still name an alias or a retired model; send the listed model instead.
        model = ResolveTextModel(requested)
            ?? throw new PluginRequestException("Unknown Mistral text model. Refresh models and select a text model.", PluginRequestFailureKind.Configuration);
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userText } },
            ["max_tokens"] = LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
        };
        if (ReasoningEffort(model) is { } effort) body["reasoning_effort"] = effort;
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
