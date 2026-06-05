using System.Text;
using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Lists the supported workflow template values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowTemplate>))]
public enum WorkflowTemplate
{
    /// <summary>
    /// Represents the cleaned text option.
    /// </summary>
    CleanedText,
    /// <summary>
    /// Represents the translation option.
    /// </summary>
    Translation,
    /// <summary>
    /// Represents the email reply option.
    /// </summary>
    EmailReply,
    /// <summary>
    /// Represents the meeting notes option.
    /// </summary>
    MeetingNotes,
    /// <summary>
    /// Represents the checklist option.
    /// </summary>
    Checklist,
    /// <summary>
    /// Represents the JSON option.
    /// </summary>
    Json,
    /// <summary>
    /// Represents the summary option.
    /// </summary>
    Summary,
    /// <summary>
    /// Represents the custom option.
    /// </summary>
    Custom
}

/// <summary>
/// Lists the supported workflow trigger kind values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowTriggerKind>))]
public enum WorkflowTriggerKind
{
    /// <summary>
    /// Represents the app option.
    /// </summary>
    App,
    /// <summary>
    /// Represents the website option.
    /// </summary>
    Website,
    /// <summary>
    /// Represents the hotkey option.
    /// </summary>
    Hotkey,
    /// <summary>
    /// Represents the global option.
    /// </summary>
    Global,
    /// <summary>
    /// Represents the manual option.
    /// </summary>
    Manual
}

/// <summary>
/// Lists the supported workflow hotkey behavior values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowHotkeyBehavior>))]
public enum WorkflowHotkeyBehavior
{
    /// <summary>
    /// Represents the start dictation option.
    /// </summary>
    StartDictation,
    /// <summary>
    /// Represents the process selected text option.
    /// </summary>
    ProcessSelectedText
}

/// <summary>
/// Represents workflow template definition data.
/// </summary>
/// <param name="Template">Template supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Description">Description supplied to the member.</param>
/// <param name="Icon">Icon supplied to the member.</param>
public sealed record WorkflowTemplateDefinition(
    WorkflowTemplate Template,
    string Name,
    string Description,
    string Icon);

/// <summary>
/// Provides workflow template catalog behavior.
/// </summary>
public static class WorkflowTemplateCatalog
{
    /// <summary>
    /// Gets the all.
    /// </summary>
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

    /// <summary>
    /// Returns the definition for.
    /// </summary>
    public static WorkflowTemplateDefinition DefinitionFor(WorkflowTemplate template) =>
        All.FirstOrDefault(definition => definition.Template == template)
        ?? All[^1];
}

/// <summary>
/// Represents workflow trigger data.
/// </summary>
public sealed record WorkflowTrigger
{
    /// <summary>
    /// Gets or sets the kind value.
    /// </summary>
    public required WorkflowTriggerKind Kind { get; init; }
    /// <summary>
    /// Gets or sets the process names value.
    /// </summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    /// <summary>
    /// Gets or sets the website patterns value.
    /// </summary>
    public IReadOnlyList<string> WebsitePatterns { get; init; } = [];
    /// <summary>
    /// Gets or sets the hotkeys value.
    /// </summary>
    public IReadOnlyList<string> Hotkeys { get; init; } = [];
    /// <summary>
    /// Gets or sets the hotkey behavior value.
    /// </summary>
    public WorkflowHotkeyBehavior HotkeyBehavior { get; init; } = WorkflowHotkeyBehavior.StartDictation;

    /// <summary>
    /// Gets whether is automatic.
    /// </summary>
    [JsonIgnore]
    public bool IsAutomatic =>
        Kind is WorkflowTriggerKind.App or WorkflowTriggerKind.Website or WorkflowTriggerKind.Hotkey;

    /// <summary>
    /// Gets whether has app bindings.
    /// </summary>
    [JsonIgnore]
    public bool HasAppBindings => ProcessNames.Count > 0;

    /// <summary>
    /// Gets whether has website bindings.
    /// </summary>
    [JsonIgnore]
    public bool HasWebsiteBindings => WebsitePatterns.Count > 0;

    /// <summary>
    /// Gets whether has hotkey bindings.
    /// </summary>
    [JsonIgnore]
    public bool HasHotkeyBindings => Hotkeys.Count > 0;

    /// <summary>
    /// Gets whether has automatic values.
    /// </summary>
    [JsonIgnore]
    public bool HasAutomaticValues => HasAppBindings || HasWebsiteBindings || HasHotkeyBindings;

    /// <summary>
    /// Gets whether has values.
    /// </summary>
    public bool HasValues => Kind switch
    {
        WorkflowTriggerKind.App => HasAutomaticValues,
        WorkflowTriggerKind.Website => HasAutomaticValues,
        WorkflowTriggerKind.Hotkey => HasAutomaticValues,
        WorkflowTriggerKind.Global => true,
        WorkflowTriggerKind.Manual => true,
        _ => false
    };

    /// <summary>
    /// Creates an app trigger.
    /// </summary>
    public static WorkflowTrigger App(params string[] processNames) =>
        new() { Kind = WorkflowTriggerKind.App, ProcessNames = Clean(processNames) };

    /// <summary>
    /// Creates a website trigger.
    /// </summary>
    public static WorkflowTrigger Website(params string[] patterns) =>
        new() { Kind = WorkflowTriggerKind.Website, WebsitePatterns = Clean(patterns) };

    /// <summary>
    /// Creates a hotkey-based workflow trigger.
    /// </summary>
    public static WorkflowTrigger Hotkey(
        IEnumerable<string> hotkeys,
        WorkflowHotkeyBehavior behavior = WorkflowHotkeyBehavior.StartDictation) =>
        new() { Kind = WorkflowTriggerKind.Hotkey, Hotkeys = Clean(hotkeys), HotkeyBehavior = behavior };

    /// <summary>
    /// Creates a hotkey-based workflow trigger.
    /// </summary>
    public static WorkflowTrigger Hotkey(params string[] hotkeys) =>
        Hotkey(hotkeys.AsEnumerable());

    /// <summary>
    /// Creates a hotkey-based workflow trigger.
    /// </summary>
    public static WorkflowTrigger Hotkey(
        WorkflowHotkeyBehavior behavior,
        params string[] hotkeys) =>
        Hotkey(hotkeys.AsEnumerable(), behavior);

    /// <summary>
    /// Creates a global trigger.
    /// </summary>
    public static WorkflowTrigger Global() =>
        new() { Kind = WorkflowTriggerKind.Global };

    /// <summary>
    /// Creates a manual trigger.
    /// </summary>
    public static WorkflowTrigger Manual() =>
        new() { Kind = WorkflowTriggerKind.Manual };

    private static IReadOnlyList<string> Clean(IEnumerable<string> values) =>
        values.Select(static value => value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Represents workflow behavior data.
/// </summary>
public sealed record WorkflowBehavior
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Gets or sets the fine tuning value.
    /// </summary>
    public string FineTuning { get; init; } = "";
    /// <summary>
    /// Gets or sets the provider override value.
    /// </summary>
    public string? ProviderOverride { get; init; }
    /// <summary>
    /// Gets or sets the model override value.
    /// </summary>
    public string? ModelOverride { get; init; }
    /// <summary>
    /// Gets or sets the input language value.
    /// </summary>
    public string? InputLanguage { get; init; }
    /// <summary>
    /// Gets or sets the selected task value.
    /// </summary>
    public string? SelectedTask { get; init; }
    /// <summary>
    /// Gets or sets the translation target value.
    /// </summary>
    public string? TranslationTarget { get; init; }
    /// <summary>
    /// Gets or sets the whisper mode override value.
    /// </summary>
    public bool? WhisperModeOverride { get; init; }
    /// <summary>
    /// Gets or sets the transcription model override value.
    /// </summary>
    public string? TranscriptionModelOverride { get; init; }
}

/// <summary>
/// Represents workflow output data.
/// </summary>
public sealed record WorkflowOutput
{
    /// <summary>
    /// Gets or sets the format value.
    /// </summary>
    public string? Format { get; init; }
    /// <summary>
    /// Gets or sets the auto enter value.
    /// </summary>
    public bool AutoEnter { get; init; }
    /// <summary>
    /// Gets or sets the target action plugin id value.
    /// </summary>
    public string? TargetActionPluginId { get; init; }

    /// <summary>
    /// Gets or sets the raw number normalization mode value stored in workflow JSON.
    /// </summary>
    [JsonPropertyName("numberNormalizationModeRaw")]
    public string? NumberNormalizationModeRaw { get; init; }

    /// <summary>
    /// Gets the parsed number normalization mode.
    /// </summary>
    [JsonIgnore]
    public WorkflowNumberNormalizationMode NumberNormalizationMode =>
        WorkflowNumberNormalizationModes.Parse(NumberNormalizationModeRaw);
}

/// <summary>
/// Represents workflow-level number normalization behavior.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowNumberNormalizationMode>))]
public enum WorkflowNumberNormalizationMode
{
    /// <summary>
    /// Uses the global number normalization setting.
    /// </summary>
    Inherit,
    /// <summary>
    /// Enables number normalization for this workflow.
    /// </summary>
    Enabled,
    /// <summary>
    /// Disables number normalization for this workflow.
    /// </summary>
    Disabled
}

/// <summary>
/// Provides conversion helpers for workflow number normalization modes.
/// </summary>
public static class WorkflowNumberNormalizationModes
{
    /// <summary>
    /// Parses a raw workflow number normalization mode value.
    /// </summary>
    public static WorkflowNumberNormalizationMode Parse(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "enabled" => WorkflowNumberNormalizationMode.Enabled,
            "disabled" => WorkflowNumberNormalizationMode.Disabled,
            _ => WorkflowNumberNormalizationMode.Inherit
        };

    /// <summary>
    /// Converts a workflow number normalization mode to its serialized value.
    /// </summary>
    public static string? ToRawValue(this WorkflowNumberNormalizationMode mode) => mode switch
    {
        WorkflowNumberNormalizationMode.Enabled => "enabled",
        WorkflowNumberNormalizationMode.Disabled => "disabled",
        _ => null
    };

    /// <summary>
    /// Converts a workflow number normalization mode to an override value.
    /// </summary>
    public static bool? OverrideValue(this WorkflowNumberNormalizationMode mode) => mode switch
    {
        WorkflowNumberNormalizationMode.Enabled => true,
        WorkflowNumberNormalizationMode.Disabled => false,
        _ => null
    };
}

/// <summary>
/// Represents workflow data.
/// </summary>
public sealed record Workflow
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the name value.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the sort order value.
    /// </summary>
    public int SortOrder { get; init; }
    /// <summary>
    /// Gets or sets the template value.
    /// </summary>
    public required WorkflowTemplate Template { get; init; }
    /// <summary>
    /// Gets or sets the trigger value.
    /// </summary>
    public required WorkflowTrigger Trigger { get; init; }
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public WorkflowBehavior Behavior { get; init; } = new();
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public WorkflowOutput Output { get; init; } = new();
    /// <summary>
    /// Gets or sets the created at value.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Gets or sets the updated at value.
    /// </summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Returns the definition.
    /// </summary>
    [JsonIgnore]
    public WorkflowTemplateDefinition Definition => WorkflowTemplateCatalog.DefinitionFor(Template);

    /// <summary>
    /// Gets whether is manually runnable.
    /// </summary>
    [JsonIgnore]
    public bool IsManuallyRunnable =>
        SystemPrompt() is not null || !string.IsNullOrWhiteSpace(Output.TargetActionPluginId);

    /// <summary>
    /// Returns the system prompt.
    /// </summary>
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
