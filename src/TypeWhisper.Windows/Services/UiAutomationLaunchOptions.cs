using System.IO;
using System.Net;
using System.Net.Http;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Describes an isolated debug-only UI automation launch.
/// </summary>
internal sealed record UiAutomationLaunchOptions
{
    private const string AutomationFlag = "--ui-automation";
    private static readonly HashSet<string> SupportedLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "de", "en", "ja", "ru", "zh-Hans" };

    /// <summary>
    /// Gets the disabled automation options.
    /// </summary>
    public static UiAutomationLaunchOptions Disabled { get; } = new();

    /// <summary>
    /// Gets the stable reference time used by deterministic screenshot fixtures.
    /// </summary>
    public static DateTime DefaultReferenceUtc { get; } =
        new(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// Gets whether UI automation mode is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets the isolated user data root.
    /// </summary>
    public string? DataRoot { get; init; }

    /// <summary>
    /// Gets the requested UI language.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Gets the release-like version shown in automated screenshots.
    /// </summary>
    public string DisplayVersion { get; init; } = "1.0.0";

    /// <summary>
    /// Gets the reference time used by deterministic screenshot fixtures.
    /// </summary>
    public DateTime ReferenceUtc { get; init; } = DefaultReferenceUtc;

    /// <summary>
    /// Gets whether the commercial fixture is active.
    /// </summary>
    public bool HasPremiumFixture { get; init; }

    /// <summary>
    /// Gets the optional ready file path.
    /// </summary>
    public string? ReadyFile { get; init; }

    /// <summary>
    /// Gets the optional local plugin registry fixture path.
    /// </summary>
    public string? PluginRegistryFile { get; init; }

    /// <summary>
    /// Gets the identifier used to isolate process-wide synchronization objects.
    /// </summary>
    public string InstanceId { get; init; } = "disabled";

    /// <summary>
    /// Records a diagnostic event beside the ready file for the isolated runner.
    /// </summary>
    public void RecordEvent(string name)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(ReadyFile))
            return;

        try
        {
            File.AppendAllText($"{ReadyFile}.events", $"{DateTime.UtcNow:O} {name}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never affect the application under test.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never affect the application under test.
        }
    }

    /// <summary>
    /// Parses UI automation arguments without accepting them in release builds.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> args,
        out UiAutomationLaunchOptions options,
        out string? error)
    {
        options = Disabled;
        error = null;

        if (!args.Contains(AutomationFlag, StringComparer.OrdinalIgnoreCase))
            return true;

#if !DEBUG
        error = "UI automation mode is available only in debug builds.";
        return false;
#else
        if (!TryReadRequiredPath(args, "--automation-data-root", out var dataRoot, out error))
            return false;

        if (!TryReadValue(args, "--automation-language", out var languageValue, out error))
            return false;

        var language = languageValue ?? "en";
        if (!SupportedLanguages.Contains(language))
        {
            error = $"Unsupported UI automation language '{language}'.";
            return false;
        }

        if (!TryReadValue(args, "--automation-display-version", out var displayVersionValue, out error))
            return false;

        var displayVersion = displayVersionValue ?? "1.0.0";
        if (string.IsNullOrWhiteSpace(displayVersion)
            || displayVersion.Length > 32
            || displayVersion.Any(char.IsWhiteSpace))
        {
            error = "The automation display version must be a short value without whitespace.";
            return false;
        }

        if (!TryReadValue(args, "--automation-instance", out var instanceIdValue, out error))
            return false;

        var instanceId = instanceIdValue ?? Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(instanceId)
            || instanceId.Length > 64
            || instanceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            error = "The automation instance identifier must contain only ASCII letters, digits, or hyphens.";
            return false;
        }

        if (!TryReadOptionalPath(args, "--automation-ready-file", out var readyFile, out error)
            || !TryReadOptionalPath(args, "--automation-plugin-registry", out var registryFile, out error))
        {
            return false;
        }

        if (!TryGetFullPath(dataRoot!, "--automation-data-root", out var fullDataRoot, out error)
            || !TryGetOptionalFullPath(readyFile, "--automation-ready-file", out var fullReadyFile, out error)
            || !TryGetOptionalFullPath(
                registryFile,
                "--automation-plugin-registry",
                out var fullRegistryFile,
                out error))
        {
            return false;
        }

        if (fullRegistryFile is not null && !File.Exists(fullRegistryFile))
        {
            error = $"The automation plugin registry fixture does not exist: {fullRegistryFile}";
            return false;
        }

        options = new UiAutomationLaunchOptions
        {
            IsEnabled = true,
            DataRoot = fullDataRoot,
            Language = language,
            DisplayVersion = displayVersion,
            HasPremiumFixture = args.Contains("--automation-premium", StringComparer.OrdinalIgnoreCase),
            ReadyFile = fullReadyFile,
            PluginRegistryFile = fullRegistryFile,
            InstanceId = instanceId
        };
        return true;
#endif
    }

    private static bool TryReadRequiredPath(
        IReadOnlyList<string> args,
        string name,
        out string? value,
        out string? error)
    {
        if (!TryReadValue(args, name, out value, out error))
            return false;

        if (!string.IsNullOrWhiteSpace(value))
            return true;

        error = $"UI automation mode requires {name} <path>.";
        return false;
    }

    private static bool TryReadOptionalPath(
        IReadOnlyList<string> args,
        string name,
        out string? value,
        out string? error)
    {
        if (!TryReadValue(args, name, out value, out error))
            return false;

        if (value is null || !string.IsNullOrWhiteSpace(value))
            return true;

        error = $"UI automation option {name} requires a non-empty path.";
        return false;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"UI automation option {name} requires a value.";
                return false;
            }

            value = args[index + 1];
            return true;
        }

        return true;
    }

    private static bool TryGetOptionalFullPath(
        string? path,
        string name,
        out string? fullPath,
        out string? error)
    {
        fullPath = null;
        error = null;
        if (path is null)
            return true;

        if (!TryGetFullPath(path, name, out var resolvedPath, out error))
            return false;

        fullPath = resolvedPath;
        return true;
    }

    private static bool TryGetFullPath(
        string path,
        string name,
        out string? fullPath,
        out string? error)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            fullPath = null;
            error = $"Invalid path for {name}: {ex.Message}";
            return false;
        }
    }
}

/// <summary>
/// Serves a local plugin registry fixture without network access.
/// </summary>
internal sealed class UiAutomationRegistryMessageHandler(string registryFile) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(registryFile, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json)
        };
    }
}
