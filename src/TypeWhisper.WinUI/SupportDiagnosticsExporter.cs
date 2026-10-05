using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.WinUI;

internal static partial class SupportDiagnosticsExporter
{
    // Capture live UI state first, then read files and write the report off the UI thread.
    internal static async Task<Report> CaptureAsync(LocalDictationSession session, WinUIHttpApi api)
    {
        var exportedAt = DateTimeOffset.UtcNow;
        var capture = new SupportDiagnosticsCapture();
        var app = capture.Try("app", () =>
        {
            using var process = Process.GetCurrentProcess();
            return new Report.AppInfo(WindowsApplicationUpdates.CurrentVersion,
                typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                WinUIProfile.DevelopmentBuild, Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds));
        });
        var system = capture.Try("system", () =>
        {
            using var process = Process.GetCurrentProcess();
            return new Report.SystemInfo("Windows", Environment.OSVersion.VersionString,
                RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription, CultureInfo.CurrentCulture.Name, Loc.Language,
                TimeZoneInfo.Local.Id, process.WorkingSet64, process.PrivateMemorySize64);
        });
        var model = capture.Try("model", session.DiagnosticModel);
        var apiInfo = capture.Try("api", () => new Report.ApiInfo(api.Enabled, api.Running, api.Port, true, api.RequireAuthentication));
        var permission = capture.Try("microphonePermission", () =>
            global::Windows.Devices.Enumeration.DeviceAccessInformation.CreateFromDeviceClass(
                global::Windows.Devices.Enumeration.DeviceClass.AudioCapture).CurrentStatus.ToString());
        var audio = capture.Try("audio", () => session.DiagnosticAudio(permission ?? "Unknown"));
        var settings = capture.Try("settings", session.DiagnosticSettings);
        var history = capture.Try("historyCount", () => new Count(session.DiagnosticHistoryCount));
        var localStates = CaptureLocalPluginStates(session.Models, session.CtcVocabulary, session.LocalPluginError);

        return await Task.Run(() =>
        {
            var plugins = capture.Try("plugins", () => Plugins(session.Packages.Store, session.PluginRuntime, localStates));
            var workflowDefaults = capture.Try("workflowDefaults", session.WorkflowDefaults.Read);
            var workflows = capture.Try("workflows", () => Report.SummarizeWorkflows(
                new ManualWorkflowStore(WinUIProfile.DataPath("workflows.json")).Read(), workflowDefaults));
            var dictionary = capture.Try("dictionaryCounts", () => ReadDictionary());
            var snippets = capture.Try("snippetCounts", () => ReadSnippets());
            var counts = new Report.CountsInfo(history?.Value,
                dictionary?.Count(entry => entry.EntryType == DictionaryEntryType.Term),
                dictionary?.Count(entry => entry.EntryType == DictionaryEntryType.Correction),
                snippets?.Length, snippets?.Count(entry => entry.IsEnabled));
            var log = capture.Try("diagnosticLog", AppDiagnostics.CaptureLog);
            return new Report(exportedAt, app, system, model, apiInfo, audio, settings, plugins, workflows, counts, log, capture.Errors);
        });
    }

    private static DictionaryEntry[] ReadDictionary()
    {
        var path = WinUIProfile.DataPath("dictionary.json");
        return File.Exists(path) ? LexiconTransfer.ReadDictionary(File.ReadAllText(path), allowPackEntries: true) : [];
    }

    private static Snippet[] ReadSnippets()
    {
        var path = WinUIProfile.DataPath("snippets.json");
        return File.Exists(path) ? LexiconTransfer.ReadSnippets(File.ReadAllText(path)) : [];
    }

    private sealed record Count(int Value);
}
