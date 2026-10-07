using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>A support snapshot containing explicit metadata, never raw preferences or user content.</summary>
public sealed record SupportDiagnosticsReport(
    DateTimeOffset ExportedAt,
    SupportDiagnosticsReport.AppInfo? App,
    SupportDiagnosticsReport.SystemInfo? System,
    SupportDiagnosticsReport.ModelInfo? Model,
    SupportDiagnosticsReport.ApiInfo? Api,
    SupportDiagnosticsReport.AudioInfo? Audio,
    SupportDiagnosticsReport.SettingsInfo? Settings,
    IReadOnlyList<SupportDiagnosticsReport.PluginInfo>? Plugins,
    SupportDiagnosticsReport.WorkflowInfo? Workflows,
    SupportDiagnosticsReport.CountsInfo? Counts,
    SupportDiagnosticsReport.LogInfo? Log,
    IReadOnlyList<SupportDiagnosticsReport.CollectionError> CollectionErrors)
{
    /// <summary>The Windows support-report schema, independent of the macOS schema.</summary>
    /// <remarks>Version 2 replaced settings.appFormattingEnabled with appAwareFormattingEnabled and added
    /// stripFinalPeriodFromStandaloneValues.</remarks>
    public int SchemaVersion => 2;

    /// <summary>App identity and process lifetime, without installation paths.</summary>
    public sealed record AppInfo(string Version, string Build, bool IsDevelopment, double UptimeSeconds);

    /// <summary>Operating system, runtime and process memory, without user or machine names.</summary>
    public sealed record SystemInfo(string Platform, string OsVersion, string OsArchitecture, string ProcessArchitecture,
        string RuntimeVersion, string Locale, string InterfaceLanguage, string TimeZone,
        long WorkingSetBytes, long PrivateMemoryBytes);

    /// <summary>The selected engine and capabilities; readiness does not imply model residency.</summary>
    public sealed record ModelInfo(string? ProviderId, string? EngineId, string? ModelId, bool Ready,
        bool Busy, bool SupportsLiveTranscription, bool SupportsTranslation);

    /// <summary>Local API availability; credentials and discovery files are never read.</summary>
    public sealed record ApiInfo(bool Enabled, bool Running, int Port, bool LoopbackOnly, bool RequiresAuthentication);

    /// <summary>Capture state and device metadata, without audio samples or persistent hardware identifiers.</summary>
    public sealed record AudioInfo(string MicrophoneAccess, bool HasCaptureDevice, bool CaptureFailed,
        bool Recording, bool PreferredInputIsSystemDefault, int UnavailablePreferredInputCount,
        IReadOnlyList<InputDeviceInfo> InputDevices, bool UsesSystemDefaultOutput, MicrophoneTestInfo? MicrophoneTest);

    /// <summary>Device labels match the microphone picker and may contain personal names; priority is one-based when explicitly configured.</summary>
    public sealed record InputDeviceInfo(string Name, bool IsDefault, int? Priority);

    /// <summary>The last microphone test's state, without its error message or captured audio.</summary>
    public sealed record MicrophoneTestInfo(bool Running, string State, bool HasWindowsFlags, bool Failed);

    /// <summary>Only known behavior choices are included; this is not a settings backup.</summary>
    public sealed record SettingsInfo(string RecordingMode, string? Language, string Task, bool LivePreviewEnabled,
        bool AutoPaste, bool LockPasteToFocusedField, bool SaveToHistory, bool SaveHistoryAudio,
        string HistoryRetentionMode, int HistoryRetentionMinutes, bool RecoveryEnabled, int RecoveryRetentionDays,
        int ModelAutoUnloadSeconds, bool WhisperModeEnabled, bool AudioDuckingEnabled, float AudioDuckingLevel,
        bool PauseMediaDuringRecording, bool SoundFeedbackEnabled, bool SpokenFeedbackEnabled,
        bool SilenceAutoStopEnabled, int SilenceAutoStopSeconds, bool TranscribeShortQuietClipsAggressively,
        bool NumberNormalizationEnabled, bool ShortUtterancePunctuationEnabled, bool AppAwareFormattingEnabled,
        bool StripFinalPeriodFromStandaloneValues,
        IReadOnlyList<string> UnavailablePreferences);

    /// <summary>Installed package state; errors are flags instead of plugin-supplied messages.</summary>
    public sealed record PluginInfo(string? Id, string? Version, bool? IsLocal, bool Enabled,
        bool HasError, bool PendingRestart, bool HasUpdateWarning, IReadOnlyList<ProviderInfo> Providers);

    /// <summary>A provider's last published readiness, without activating or invoking the plugin.</summary>
    public sealed record ProviderInfo(string Kind, string? Id, bool Ready, string? SelectedModelId);

    /// <summary>Workflow configuration without names, IDs, prompts, app bindings or website patterns.</summary>
    public sealed record WorkflowInfo(int Total, int Enabled, IReadOnlyList<WorkflowMetadata> EnabledWorkflows,
        string? DefaultProviderId, string? DefaultModelId);

    /// <summary>Explicit technical metadata of one enabled workflow.</summary>
    public sealed record WorkflowMetadata(string Trigger, string Template, string? ProviderId, string? ModelId,
        string? TranscriptionModelId, int AppBindingCount, int WebsiteBindingCount, int HotkeyCount,
        bool HasCustomInstructions, bool AutoEnter, string? ActionPluginId, string NumberNormalizationMode,
        bool UsesDefaultLlm);

    /// <summary>Data quantities without any stored text.</summary>
    public sealed record CountsInfo(int? HistoryRecords, int? DictionaryTerms, int? DictionaryCorrections,
        int? Snippets, int? EnabledSnippets);

    /// <summary>Retained, allowlisted log entries; disabled logging contributes no entries.</summary>
    public sealed record LogInfo(bool Enabled, int RetentionDays, IReadOnlyList<DiagnosticLogLine> Entries);

    /// <summary>An unavailable section identified only by exception type and HRESULT.</summary>
    public sealed record CollectionError(string Section, string? ErrorType, string? HResult);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Atomically writes a single JSON support report to the user's chosen destination.</summary>
    public void Export(string destination) => AtomicFileWriter.WriteAllText(destination, ToJson());

    /// <summary>Serializes metadata and reapplies the log's admission rules at the export boundary.</summary>
    public string ToJson() => JsonSerializer.Serialize(this with
    {
        Log = Log is { } log ? log with
        {
            Entries = log.Enabled ? log.Entries.Select(DiagnosticLogFile.Admit).ToArray() : []
        } : null
    }, Json);

    /// <summary>Projects workflows instead of serializing content-bearing workflow objects.</summary>
    public static WorkflowInfo SummarizeWorkflows(IReadOnlyList<Workflow> workflows, WorkflowLlmSelection? defaults = null) => new(
        workflows.Count, workflows.Count(workflow => workflow.IsEnabled),
        workflows.Where(workflow => workflow.IsEnabled).Select(workflow =>
        {
            var effective = WorkflowLlmDefaults.Apply(workflow, defaults);
            return new WorkflowMetadata(workflow.Trigger.Kind.ToString(), workflow.Template.ToString(),
                Identifier(effective.Behavior.ProviderOverride), Identifier(effective.Behavior.ModelOverride),
                Identifier(workflow.Behavior.TranscriptionModelOverride), workflow.Trigger.ProcessNames.Count,
                workflow.Trigger.WebsitePatterns.Count, workflow.Trigger.Hotkeys.Count,
                !string.IsNullOrWhiteSpace(workflow.Behavior.FineTuning), workflow.Output.AutoEnter,
                Identifier(workflow.Output.TargetActionPluginId), workflow.Output.NumberNormalizationMode.ToString(),
                workflow.Template != WorkflowTemplate.Dictation && workflow.Behavior.ProviderOverride == WorkflowLlmDefaults.Inherit);
        }).ToArray(), Identifier(defaults?.Provider), Identifier(defaults?.Model));

    /// <summary>Keeps technical IDs, including provider/model IDs, while omitting paths, URLs and free text.</summary>
    // Namespaced IDs and relative paths cannot be distinguished without a trusted catalog.
    // Omit directory separators and Windows drive prefixes on every operating system.
    public static string? Identifier(string? value) => value is null ? null :
        value.Length is > 0 and <= 160 && char.IsAsciiLetterOrDigit(value[0]) && !value.Contains("..") &&
        !(value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or ':')
            ? value : "[omitted]";
}

/// <summary>Lets unavailable diagnostic sections fail independently without exporting exception messages.</summary>
public sealed class SupportDiagnosticsCapture
{
    private readonly List<SupportDiagnosticsReport.CollectionError> _errors = [];

    /// <summary>The failures collected so far.</summary>
    public IReadOnlyList<SupportDiagnosticsReport.CollectionError> Errors => _errors.ToArray();

    /// <summary>Captures a section or records its technical failure and leaves the section absent.</summary>
    public T? Try<T>(string section, Func<T?> collect) where T : class
    {
        try { return collect(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = DiagnosticLogFile.Admit(new(DateTimeOffset.UtcNow, "diagnostics.collection.failed",
                Error: ex.GetType().FullName, HResult: $"0x{ex.HResult:X8}"));
            _errors.Add(new(section, error.Error, error.HResult));
            return null;
        }
    }
}
