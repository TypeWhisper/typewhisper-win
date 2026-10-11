using System.Text.Json;

namespace TypeWhisper.Presentation;

/// <summary>A process that owned the foreground window when a browser extension reported.</summary>
/// <param name="ProcessId">Foreground process identifier.</param>
/// <param name="ProcessName">Process name without extension, as workflows and /v1/rules use it.</param>
public sealed record BrowserForegroundProcess(int ProcessId, string ProcessName);

/// <summary>The active tab a TypeWhisper browser extension reported, assigned to the foreground process at arrival.</summary>
/// <param name="ClientId">The reporting extension's identifier.</param>
/// <param name="ProcessId">The foreground process when the report arrived.</param>
/// <param name="ProcessName">That process's name.</param>
/// <param name="Url">An http, https or file URL; null when the active tab shows no website.</param>
/// <param name="Title">The trimmed tab title, or null.</param>
/// <param name="ReceivedAt">When the report arrived.</param>
public sealed record BrowserExtensionReport(string ClientId, int ProcessId, string ProcessName, string? Url, string? Title,
    DateTimeOffset ReceivedAt);

/// <summary>Keeps the latest browser extension report in memory, following the macOS app's
/// BrowserExtensionContextStore. Extensions repeat their report every minute while the browser stays in front.</summary>
public sealed class BrowserExtensionContextStore(Func<BrowserForegroundProcess?> foreground, int ownProcessId,
    Func<DateTimeOffset>? now = null)
{
    /// <summary>A report that is not repeated may come from an extension that was removed or stopped.</summary>
    public static TimeSpan ReportLifetime { get; } = TimeSpan.FromSeconds(180);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private BrowserExtensionReport? _report;

    /// <summary>Records a report. A focused report replaces the previous one and belongs to the foreground process;
    /// it is ignored while TypeWhisper itself or no window is in front. A report without focus only removes this
    /// extension's own report, so a browser that loses focus cannot clear the one of the browser that came to the front.</summary>
    /// <returns>The stored report, or null when nothing was stored.</returns>
    public BrowserExtensionReport? Report(string clientId, bool focused, string? url, string? title)
    {
        if (!focused)
        {
            lock (_lock) if (_report?.ClientId == clientId) _report = null;
            return null;
        }
        if (foreground() is not { ProcessId: > 0 } process || process.ProcessId == ownProcessId ||
            string.IsNullOrEmpty(process.ProcessName)) return null;
        var report = new BrowserExtensionReport(clientId, process.ProcessId, process.ProcessName, url, title, _now());
        lock (_lock) _report = report;
        return report;
    }

    /// <summary>The current report for this dictation target, or null when none belongs to the process.</summary>
    public BrowserExtensionReport? Current(int processId, string processName)
    {
        BrowserExtensionReport? report;
        lock (_lock) report = _report;
        return report is not null && report.ProcessId == processId &&
            string.Equals(report.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
            _now() - report.ReceivedAt <= ReportLifetime ? report : null;
    }

    /// <summary>The website host for a dictation target. A current report decides alone, also for browsers the
    /// UI Automation read does not know; a report without a URL means no website. Without a report the
    /// <paramref name="fallback"/> read runs.</summary>
    public async Task<string?> CaptureHostAsync(int processId, string processName, Func<Task<string?>> fallback)
    {
        if (Current(processId, processName) is { } report) return BrowserWorkflowContext.NormalizeHost(report.Url);
        return await fallback().ConfigureAwait(false);
    }

    /// <summary>Accepts http, https and file URLs below 2048 characters, like the macOS app.
    /// New tabs and browser pages such as chrome://settings return null.</summary>
    public static string? NormalizeUrl(string? value)
    {
        var text = value?.Trim();
        if (text is null || text.Length is <= 3 or >= 2048 || text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file")) return null;
        return text;
    }

    /// <summary>Trims a tab title to at most 512 characters; an empty title returns null.</summary>
    public static string? NormalizeTitle(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length <= 512) return text;
        return text[..(char.IsHighSurrogate(text[511]) ? 511 : 512)];
    }
}

/// <summary>PUT /v1/browser/context: a browser extension reports the active tab of its window.</summary>
public static class LocalApiBrowserContext
{
    /// <summary>The route path.</summary>
    public const string Path = "/v1/browser/context";
    private static readonly string[] Fields = ["client_id", "focused", "url", "title"];

    /// <summary>Handles the route, or returns null for any other path.</summary>
    public static LocalApiResponse? Handle(LocalApiRequest request, BrowserExtensionContextStore store, CancellationToken ct = default)
    {
        if (request.Path != Path) return null;
        ct.ThrowIfCancellationRequested();
        if (request.Method != "PUT") return Error(405, "Use PUT.");
        if (request.Query.Count != 0) return Error(400, "This endpoint accepts no query parameters.");
        string clientId;
        bool focused;
        string? url, title;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(400, "Invalid JSON body.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!names.Add(field.Name) || !Fields.Contains(field.Name, StringComparer.Ordinal))
                    return Error(400, "Unknown or duplicate JSON field.");
            if (!root.TryGetProperty("client_id", out var client) || client.ValueKind != JsonValueKind.String ||
                client.GetString()!.Trim() is not { Length: >= 1 and <= 128 } trimmed)
                return Error(400, "Missing or invalid 'client_id'.");
            clientId = trimmed;
            if (!root.TryGetProperty("focused", out var focus) || focus.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Error(400, "'focused' must be a boolean.");
            focused = focus.GetBoolean();
            if (!OptionalString(root, "url", out url) || !OptionalString(root, "title", out title))
                return Error(400, "'url' and 'title' must be strings or null.");
        }
        catch (JsonException) { return Error(400, "Invalid JSON body."); }
        var report = store.Report(clientId, focused, BrowserExtensionContextStore.NormalizeUrl(url),
            BrowserExtensionContextStore.NormalizeTitle(title));
        return LocalApiResponse.Json(200, new
        {
            status = !focused ? "cleared" : report is not null ? "active" : "ignored",
            app_bundle_id = report?.ProcessName, url = report?.Url
        });
    }

    private static bool OptionalString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return true;
    }

    private static LocalApiResponse Error(int status, string message) => LocalApiResponse.Json(status,
        new { error = new { code = status == 400 ? "bad_request" : "method_not_allowed", message } });
}
