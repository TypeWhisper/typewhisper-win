using System.Text.RegularExpressions;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// IModuleDiagnostics for one module. Its events are named module.<module>.<stage>, apart from the host's own
// modules.* events, and follow the log's event name rules, so a module can neither write text into the log nor
// pass an event off as the host's. Exceptions go through the log's failure path, which keeps no messages.
internal sealed partial class ModuleDiagnostics : IModuleDiagnostics
{
    internal const string EventPrefix = "module.";
    internal const string InvalidStage = "invalid-stage";
    internal const int MaximumStageLength = 48;
    // With the prefix, the separator and the longest stage, this stays within the log's 80 characters per event name.
    internal const int MaximumMarkerLength = 24;

    private readonly string _prefix;
    private readonly Action<string> _write;
    private readonly Action<string, Exception> _writeFailure;

    private ModuleDiagnostics(string marker, Action<string> write, Action<string, Exception> writeFailure)
    {
        _prefix = EventPrefix + marker + ".";
        _write = write;
        _writeFailure = writeFailure;
    }

    // Null when the module's identifier cannot name log events.
    internal static ModuleDiagnostics? For(string? moduleId, Action<string> write, Action<string, Exception> writeFailure) =>
        Marker(moduleId) is { } marker ? new(marker, write, writeFailure) : null;

    // The module's identifier as one part of an event name: lowercased, letters and digits with single hyphens.
    internal static string? Marker(string? moduleId)
    {
        if (moduleId is null || moduleId.Length > MaximumMarkerLength) return null;
        var marker = moduleId.ToLowerInvariant();
        return MarkerPattern().IsMatch(marker) ? marker : null;
    }

    internal static bool IsValidStage(string? stage) => stage is { Length: > 0 and <= MaximumStageLength } && StagePattern().IsMatch(stage);

    internal string EventName(string? stage) => _prefix + (IsValidStage(stage) ? stage : InvalidStage);

    public void Write(string stage) => _write(EventName(stage));

    public void WriteFailure(string stage, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        _writeFailure(EventName(stage), error);
    }

    // \z, because $ would also accept a trailing line break.
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:[.\-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagePattern();
}

// The host as one module sees it: the shared host with that module's own diagnostics.
internal sealed class ModuleScopedHost(IModuleHost host, IModuleDiagnostics? diagnostics) : IModuleHost
{
    internal IModuleHost Shared => host;

    public string HostVersion => host.HostVersion;
    public string InterfaceLanguage => host.InterfaceLanguage;
    public IHistoryReading? HistoryReading => host.HistoryReading;
    public IManagedPolicyReading? ManagedPolicyReading => host.ManagedPolicyReading;
    public IPremiumAccessReading? PremiumAccessReading => host.PremiumAccessReading;
    public IUserDataLifecycle? UserDataLifecycle => host.UserDataLifecycle;
    public IAudioDiarization? AudioDiarization => host.AudioDiarization;
    public IHistorySearching? HistorySearching => host.HistorySearching;
    public ILanguageModelAnswering? LanguageModelAnswering => host.LanguageModelAnswering;
    public IModuleDiagnostics? Diagnostics => diagnostics;
    public ISpeechInput? SpeechInput => host.SpeechInput;
    public Task WhenStarted => host.WhenStarted;
    public void ShowSettingsSection(string sectionId) => host.ShowSettingsSection(sectionId);
    public void ShowPanel(string id) => host.ShowPanel(id);
    public void HidePanel(string id) => host.HidePanel(id);
    public void TogglePanel(string id) => host.TogglePanel(id);
    public bool IsPanelVisible(string id) => host.IsPanelVisible(id);
}
