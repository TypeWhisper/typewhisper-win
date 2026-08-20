using System.Text.Json;
using FlaUI.Core.AutomationElements;

namespace TypeWhisper.UiAutomation;

internal static class ScreenshotCatalog
{
    private static readonly IReadOnlyList<RouteCapture> AppRoutes =
    [
        new("Dashboard", "dashboard"),
        new("Statistics", "statistics"),
        new("Dictation", "dictation"),
        new("Shortcuts", "shortcuts"),
        new("FileTranscription", "file-transcription"),
        new("Recovery", "recovery"),
        new("Recorder", "recorder"),
        new("History", "history"),
        new("Dictionary", "dictionary"),
        new("Snippets", "snippets"),
        new("Workflows", "workflows"),
        new("General", "general"),
        new("Appearance", "appearance"),
        new("Advanced", "advanced"),
        new("Premium", "premium-required"),
        new("License", "license"),
        new("About", "about")
    ];

    public static void CaptureApp(TypeWhisperAutomationSession session, string outputDirectory)
    {
        foreach (var route in AppRoutes)
        {
            session.InvokeAndWait(route.AutomationId, $"SettingsSection.{route.AutomationId}");
            CaptureWindow(session, outputDirectory, route.FileName);
        }

        session.InvokeAndWait("Integrations", "SettingsSection.Integrations");
        session.Invoke("IntegrationsTabInstalled");
        session.WaitForUiToSettle();
        CaptureWindow(session, outputDirectory, "integrations-installed");

        session.Invoke("IntegrationsTabDiscover");
        session.WaitForUiToSettle();
        CaptureWindow(session, outputDirectory, "integrations-marketplace");
    }

    public static void CapturePremium(TypeWhisperAutomationSession session, string outputDirectory)
    {
        session.InvokeAndWait("Premium", "SettingsSection.Premium");
        CaptureWindow(session, outputDirectory, "premium-active");
    }

    public static int CapturePlugins(TypeWhisperAutomationSession session, string outputDirectory)
    {
        session.InvokeAndWait("Integrations", "SettingsSection.Integrations");
        session.Invoke("IntegrationsTabInstalled");
        session.WaitForUiToSettle();

        var settingsIds = session.FindAutomationIds("IntegrationsSettings.");
        foreach (var settingsId in settingsIds)
        {
            session.ScrollAndInvoke(settingsId, "IntegrationsInstalledScroll");
            var window = session.WaitForWindow("PluginSettingsWindow");
            var pluginId = settingsId["IntegrationsSettings.".Length..];
            session.RequireFullyVisibleScrollContent("PluginSettingsScroll", window);
            session.Capture(window, Path.Join(outputDirectory, "plugins", $"{pluginId}.png"));
            window.Close();
            session.WaitForUiToSettle();
        }

        return settingsIds.Count;
    }

    public static void Smoke(TypeWhisperAutomationSession session)
    {
        foreach (var route in AppRoutes.Append(new RouteCapture("Integrations", "integrations")))
            session.InvokeAndWait(route.AutomationId, $"SettingsSection.{route.AutomationId}");
    }

    public static void RunFlow(
        TypeWhisperAutomationSession session,
        string flowPath,
        string outputDirectory)
    {
        var flow = JsonSerializer.Deserialize<AutomationFlow>(
            File.ReadAllText(flowPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Could not deserialize automation flow '{flowPath}'.");

        foreach (var step in flow.Steps)
        {
            switch (step.Action.ToLowerInvariant())
            {
                case "wait":
                    session.WaitForElement(RequireAutomationId(step));
                    break;
                case "invoke":
                    session.Invoke(RequireAutomationId(step));
                    break;
                case "click":
                    session.Click(RequireAutomationId(step));
                    break;
                case "capture":
                {
                    var automationId = RequireAutomationId(step);
                    if (string.IsNullOrWhiteSpace(step.File))
                        throw new InvalidDataException("A capture step requires 'file'.");
                    var outputPath = ResolveOutputPath(outputDirectory, step.File);
                    session.Capture(session.WaitForElement(automationId), outputPath);
                    break;
                }
                case "close":
                {
                    var automationId = RequireAutomationId(step);
                    var window = session.WaitForWindow(automationId);
                    window.Close();
                    break;
                }
                case "settle":
                    session.WaitForUiToSettle();
                    break;
                default:
                    throw new InvalidDataException($"Unsupported automation action '{step.Action}'.");
            }
        }
    }

    private static void CaptureWindow(
        TypeWhisperAutomationSession session,
        string outputDirectory,
        string fileName) =>
        session.Capture(session.SettingsWindow, Path.Join(outputDirectory, $"{fileName}.png"));

    private static string RequireAutomationId(AutomationStep step) =>
        string.IsNullOrWhiteSpace(step.AutomationId)
            ? throw new InvalidDataException($"Automation action '{step.Action}' requires 'automationId'.")
            : step.AutomationId;

    private static string ResolveOutputPath(string outputDirectory, string relativePath)
    {
        var root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Join(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Capture path '{relativePath}' leaves the output directory.");
        return path;
    }

    private sealed record RouteCapture(string AutomationId, string FileName);

    private sealed record AutomationFlow
    {
        public IReadOnlyList<AutomationStep> Steps { get; init; } = [];
    }

    private sealed record AutomationStep
    {
        public string Action { get; init; } = "";
        public string? AutomationId { get; init; }
        public string? File { get; init; }
    }
}
