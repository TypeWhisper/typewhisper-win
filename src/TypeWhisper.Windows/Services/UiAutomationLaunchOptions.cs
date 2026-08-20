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

        var language = ReadValue(args, "--automation-language") ?? "en";
        if (!SupportedLanguages.Contains(language))
        {
            error = $"Unsupported UI automation language '{language}'.";
            return false;
        }

        var displayVersion = ReadValue(args, "--automation-display-version") ?? "1.0.0";
        if (displayVersion.Length > 32 || displayVersion.Any(char.IsWhiteSpace))
        {
            error = "The automation display version must be a short value without whitespace.";
            return false;
        }

        var instanceId = ReadValue(args, "--automation-instance") ?? Guid.NewGuid().ToString("N");
        if (instanceId.Length > 64 || instanceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            error = "The automation instance identifier must contain only ASCII letters, digits, or hyphens.";
            return false;
        }

        var readyFile = ReadValue(args, "--automation-ready-file");
        var registryFile = ReadValue(args, "--automation-plugin-registry");
        if (!string.IsNullOrWhiteSpace(registryFile) && !File.Exists(registryFile))
        {
            error = $"The automation plugin registry fixture does not exist: {registryFile}";
            return false;
        }

        options = new UiAutomationLaunchOptions
        {
            IsEnabled = true,
            DataRoot = Path.GetFullPath(dataRoot!),
            Language = language,
            DisplayVersion = displayVersion,
            HasPremiumFixture = args.Contains("--automation-premium", StringComparer.OrdinalIgnoreCase),
            ReadyFile = string.IsNullOrWhiteSpace(readyFile) ? null : Path.GetFullPath(readyFile),
            PluginRegistryFile = string.IsNullOrWhiteSpace(registryFile) ? null : Path.GetFullPath(registryFile),
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
        value = ReadValue(args, name);
        error = null;
        if (!string.IsNullOrWhiteSpace(value))
            return true;

        error = $"UI automation mode requires {name} <path>.";
        return false;
    }

    private static string? ReadValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                continue;

            return index + 1 < args.Count ? args[index + 1] : null;
        }

        return null;
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
