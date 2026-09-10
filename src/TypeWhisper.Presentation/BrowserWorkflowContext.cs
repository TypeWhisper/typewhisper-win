using System.Globalization;

namespace TypeWhisper.Presentation;

/// <summary>The original foreground window and process for one dictation.</summary>
public sealed record BrowserCaptureTarget(nint WindowHandle, int ProcessId, string ProcessName);

/// <summary>Validates browser chrome identities and retains only an HTTP(S) hostname for workflow context.</summary>
public static class BrowserWorkflowContext
{
    /// <summary>Only browsers with an explicitly supported browser-chrome structure are inspected.</summary>
    public static bool IsSupportedBrowser(string processName) => processName.ToLowerInvariant() is "chrome" or "chromium" or "msedge" or "brave" or "firefox";

    /// <summary>Rejects browser document roots before visiting their children or reading values.</summary>
    public static bool CanInspectSubtree(int controlType, string automationId) => controlType != 50030 && automationId != "RootWebArea";

    /// <summary>Requires a known address-bar identity inside its known browser toolbar; page elements are never candidates.</summary>
    public static bool IsAddressBar(string processName, string automationId, string toolbarId, string shortcut,
        bool focused, bool password, bool offscreen)
    {
        if (focused || password || offscreen) return false;
        return processName.ToLowerInvariant() switch
        {
            "chrome" or "chromium" or "msedge" or "brave" => automationId == "view_1012" && toolbarId == "view_1000"
                && shortcut.Equals("Ctrl+L", StringComparison.OrdinalIgnoreCase),
            "firefox" => automationId == "urlbar-input" && toolbarId == "nav-bar",
            _ => false
        };
    }

    /// <summary>Normalizes an address-bar value, dropping credentials, path, query and fragment. Invalid or non-web addresses return null.</summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return null;
        var text = value.Trim();
        if (text.Any(char.IsWhiteSpace) || text.Contains('\\')) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns) return null;
        try
        {
            var host = new IdnMapping().GetAscii(uri.DnsSafeHost.TrimEnd('.')).ToLowerInvariant();
            if (host.Length > 253 || !host.Contains('.') || host.Split('.').Any(part => part.Length is < 1 or > 63 ||
                part[0] == '-' || part[^1] == '-' || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))) return null;
            return host;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Validates a domain rule; a plain domain and its optional wildcard both include subdomains, following the Core matcher.</summary>
    public static string? NormalizePattern(string value)
    {
        var text = value.Trim();
        var wildcard = text.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard) text = text[2..];
        if (text.IndexOfAny(['/', '\\', '?', '#', '@', ':', '*']) >= 0) return null;
        var host = NormalizeHost(text);
        return host is null ? null : wildcard ? "*." + host : host;
    }
}

/// <summary>Bounds a read-only browser probe to one outstanding worker, including after timeout or cancellation.</summary>
public sealed class BrowserTargetCapture(Func<BrowserCaptureTarget, CancellationToken, string?> read, TimeSpan timeout)
{
    private int _busy;

    /// <summary>Returns one captured hostname, or null on unavailable context/timeout. Caller cancellation discards even a late successful result.</summary>
    public async Task<string?> CaptureAsync(BrowserCaptureTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.WindowHandle == 0 || target.ProcessId <= 0 || !BrowserWorkflowContext.IsSupportedBrowser(target.ProcessName) ||
            Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeToken = deadline.Token;
        deadline.CancelAfter(timeout);
        var task = Task.Run(() =>
        {
            try { probeToken.ThrowIfCancellationRequested(); return read(target, probeToken); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            finally { deadline.Dispose(); Interlocked.Exchange(ref _busy, 0); }
        });
        try
        {
            var value = await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return probeToken.IsCancellationRequested ? null : BrowserWorkflowContext.NormalizeHost(value);
        }
        catch (TimeoutException) { return null; }
    }
}
