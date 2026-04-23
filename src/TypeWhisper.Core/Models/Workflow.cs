using System.Text;
using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowTemplate>))]
public enum WorkflowTemplate
{
    CleanedText,
    Translation,
    EmailReply,
    MeetingNotes,
    Checklist,
    Json,
    Summary,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkflowTriggerKind>))]
public enum WorkflowTriggerKind
{
    App,
    Website,
    Hotkey
}

public sealed record WorkflowTemplateDefinition(
    WorkflowTemplate Template,
    string Name,
    string Description,
    string Icon);

public static class WorkflowTemplateCatalog
{
    public static IReadOnlyList<WorkflowTemplateDefinition> All { get; } =
    [
        new(WorkflowTemplate.CleanedText, "Cleaned Text", "Clean up dictated text for readability and punctuation.", "Text"),
        new(WorkflowTemplate.Translation, "Translation", "Translate dictated text into the target language.", "Globe"),
        new(WorkflowTemplate.EmailReply, "Email Reply", "Turn dictated notes into a reply email.", "Mail"),
        new(WorkflowTemplate.MeetingNotes, "Meeting Notes", "Structure dictated notes into a meeting summary.", "Notes"),
        new(WorkflowTemplate.Checklist, "Checklist", "Extract action items into a checklist.", "Check"),
        new(WorkflowTemplate.Json, "JSON", "Extract structured data as JSON.", "Json"),
        new(WorkflowTemplate.Summary, "Summary", "Condense dictated text into a concise summary.", "Summary"),
        new(WorkflowTemplate.Custom, "Custom Workflow", "Start with a flexible workflow draft.", "Custom")
    ];

    public static WorkflowTemplateDefinition DefinitionFor(WorkflowTemplate template) =>
        All.FirstOrDefault(definition => definition.Template == template)
        ?? All[^1];
}

public sealed record WorkflowTrigger
{
    public required WorkflowTriggerKind Kind { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public IReadOnlyList<string> WebsitePatterns { get; init; } = [];
    public IReadOnlyList<string> Hotkeys { get; init; } = [];

    public bool HasValues => Kind switch
    {
        WorkflowTriggerKind.App => ProcessNames.Count > 0,
        WorkflowTriggerKind.Website => WebsitePatterns.Count > 0,
        WorkflowTriggerKind.Hotkey => Hotkeys.Count > 0,
        _ => false
    };

    public static WorkflowTrigger App(params string[] processNames) =>
        new() { Kind = WorkflowTriggerKind.App, ProcessNames = Clean(processNames) };

    public static WorkflowTrigger Website(params string[] patterns) =>
        new() { Kind = WorkflowTriggerKind.Website, WebsitePatterns = Clean(patterns) };

    public static WorkflowTrigger Hotkey(params string[] hotkeys) =>
        new() { Kind = WorkflowTriggerKind.Hotkey, Hotkeys = Clean(hotkeys) };

    private static IReadOnlyList<string> Clean(IEnumerable<string> values) =>
        values.Select(static value => value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed record WorkflowBehavior
{
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string FineTuning { get; init; } = "";
    public string? ProviderOverride { get; init; }
    public string? ModelOverride { get; init; }
    public string? InputLanguage { get; init; }
    public string? SelectedTask { get; init; }
    public string? TranslationTarget { get; init; }
    public bool? WhisperModeOverride { get; init; }
    public string? TranscriptionModelOverride { get; init; }
}

public sealed record WorkflowOutput
{
    public string? Format { get; init; }
    public bool AutoEnter { get; init; }
    public string? TargetActionPluginId { get; init; }
}

public sealed record Workflow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public required WorkflowTemplate Template { get; init; }
    public required WorkflowTrigger Trigger { get; init; }
    public WorkflowBehavior Behavior { get; init; } = new();
    public WorkflowOutput Output { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    [JsonIgnore]
    public WorkflowTemplateDefinition Definition => WorkflowTemplateCatalog.DefinitionFor(Template);

    public string? SystemPrompt(
        string? fallbackTranslationTarget = null,
        string? detectedLanguage = null,
        string? configuredLanguage = null)
    {
        var languageHint = BuildLanguageHint(detectedLanguage, configuredLanguage);
        var settingsInstruction = BuildSettingsInstruction();
        var fineTuningInstruction = BuildFineTuningInstruction();
        var outputInstruction = BuildOutputInstruction();

        return Template switch
        {
            WorkflowTemplate.CleanedText =>
                "Clean up the dictated text for readability. Fix punctuation, grammar, and formatting while preserving the original meaning and language. Return only the cleaned text."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.Translation =>
                $"Translate the dictated text into {ResolveTranslationTarget(fallbackTranslationTarget)}. Preserve meaning, names, and domain-specific terminology unless instructed otherwise. Return only the translated text."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.EmailReply =>
                "Turn the dictated text into a complete reply email. Use an appropriate greeting and closing, keep the same language as the source unless instructed otherwise, and return only the email body."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.MeetingNotes =>
                "Restructure the dictated text into clear meeting notes with concise sections, decisions, and action items where applicable. Return only the final notes."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.Checklist =>
                "Extract the actionable items from the dictated text and return them as a checklist. Keep the source language unless instructed otherwise."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.Json =>
                "Extract structured information from the dictated text and return valid JSON only. Do not wrap the JSON in markdown fences."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.Summary =>
                "Summarize the dictated text into a concise, accurate summary. Preserve important facts and keep the source language unless instructed otherwise. Return only the summary."
                + languageHint + settingsInstruction + fineTuningInstruction + outputInstruction,
            WorkflowTemplate.Custom => BuildCustomPrompt(languageHint, settingsInstruction, fineTuningInstruction, outputInstruction),
            _ => null
        };
    }

    private string ResolveTranslationTarget(string? fallbackTranslationTarget) =>
        FirstNonBlank(
            GetSetting("targetLanguage"),
            GetSetting("target"),
            Behavior.TranslationTarget,
            fallbackTranslationTarget,
            "English")!;

    private string? BuildCustomPrompt(
        string languageHint,
        string settingsInstruction,
        string fineTuningInstruction,
        string outputInstruction)
    {
        var instruction = FirstNonBlank(
            GetSetting("instruction"),
            GetSetting("goal"),
            GetSetting("prompt"),
            Behavior.FineTuning);

        if (string.IsNullOrWhiteSpace(instruction))
            return null;

        return "Apply the following workflow instruction to the dictated text and return only the final result:"
               + Environment.NewLine
               + instruction.Trim()
               + languageHint
               + settingsInstruction
               + fineTuningInstruction
               + outputInstruction;
    }

    private string BuildSettingsInstruction()
    {
        var omittedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "instruction",
            "goal",
            "prompt",
            "targetLanguage",
            "target"
        };

        var relevant = Behavior.Settings
            .Where(pair => !omittedKeys.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (relevant.Count == 0)
            return "";

        var builder = new StringBuilder()
            .AppendLine()
            .AppendLine("Additional workflow settings:");

        foreach (var (key, value) in relevant)
            builder.Append("- ").Append(key).Append(": ").AppendLine(value.Trim());

        return builder.ToString().TrimEnd();
    }

    private string BuildFineTuningInstruction()
    {
        var trimmed = Behavior.FineTuning.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? ""
            : $"{Environment.NewLine}Fine-tuning:{Environment.NewLine}{trimmed}";
    }

    private string BuildOutputInstruction()
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(Output.Format))
            lines.Add($"Return the result as {Output.Format.Trim()}.");

        if (!string.IsNullOrWhiteSpace(Output.TargetActionPluginId))
            lines.Add("Return only the transformed text result without commentary.");

        return lines.Count == 0
            ? ""
            : $"{Environment.NewLine}Output requirements:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    private static string BuildLanguageHint(string? detectedLanguage, string? configuredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(detectedLanguage))
            return $"{Environment.NewLine}Detected source language: {detectedLanguage}.";

        if (!string.IsNullOrWhiteSpace(configuredLanguage))
            return $"{Environment.NewLine}Configured source language: {configuredLanguage}.";

        return "";
    }

    private string? GetSetting(string key) =>
        Behavior.Settings.TryGetValue(key, out var value) ? value : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
