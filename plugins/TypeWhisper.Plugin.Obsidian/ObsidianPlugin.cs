using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
#if WINDOWS
using System.Windows.Controls;
#endif
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Obsidian;

/// <summary>
/// Provides obsidian plugin behavior.
/// </summary>
public sealed partial class ObsidianPlugin : IActionPlugin, IPluginTextSettings
{
    private IPluginHostServices? _host;

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.obsidian";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Obsidian";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Gets the action id.
    /// </summary>
    public string ActionId => "save-to-obsidian";
    /// <summary>
    /// Gets the action name.
    /// </summary>
    public string ActionName => "Save to Obsidian";
    /// <summary>
    /// Gets the action icon.
    /// </summary>
    public string? ActionIcon => "\ud83d\udcdd";

    internal IPluginHostServices? Host => _host;

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync() { _host = null; return Task.CompletedTask; }

#if WINDOWS
    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new ObsidianSettingsView(this);
#endif

    /// <summary>
    /// Performs execute asynchronously.
    /// </summary>
    public async Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_host is null)
            return new ActionResult(false, "Plugin not activated");

        var vaultPath = _host.GetSetting<string>("vault-path");
        if (string.IsNullOrWhiteSpace(vaultPath))
            return new ActionResult(false, "No Obsidian vault configured. Please set a vault path in the plugin settings.");

        if (!Directory.Exists(vaultPath))
            return new ActionResult(false, $"Vault path not found: {vaultPath}");

        var subfolder = _host.GetSetting<string>("subfolder") ?? "TypeWhisper";
        var dailyNoteMode = _host.GetSetting<bool>("daily-note-mode");
        var filenameTemplate = _host.GetSetting<string>("filename-template");
        if (string.IsNullOrWhiteSpace(filenameTemplate))
            filenameTemplate = "{{date}} {{time}} Transcription";

#if !WINDOWS
        if (dailyNoteMode)
            return new(false, "Daily-note append is not available in this host. Set Note mode to new-note in plugin settings.");
        var now = DateTime.Now;
        try
        {
            var savedPath = await ObsidianNoteWriter.WriteAsync(vaultPath, subfolder,
                BuildFilename(filenameTemplate, context, now), BuildNoteContent(input, context, now), ct);
            return new(true, "Saved to " + Path.GetFileName(savedPath));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "The note could not be saved. Check the vault path, note folder and write access. Your review text is unchanged."); }
#else
        var now = DateTime.Now;
        var targetDir = Path.Combine(vaultPath, subfolder);
        Directory.CreateDirectory(targetDir);

        string filePath;
        string filename;
        string content;

        if (dailyNoteMode)
        {
            filename = $"{now:yyyy-MM-dd}.md";
            filePath = Path.Combine(targetDir, filename);

            var entry = BuildDailyNoteEntry(input, context, now);

            if (File.Exists(filePath))
            {
                // Append to existing daily note
                await File.AppendAllTextAsync(filePath, entry, Encoding.UTF8, ct);
            }
            else
            {
                // Create new daily note with header
                var header = $"# {now:yyyy-MM-dd}\n\n";
                await File.WriteAllTextAsync(filePath, header + entry, Encoding.UTF8, ct);
            }
        }
        else
        {
            filename = BuildFilename(filenameTemplate, context, now) + ".md";
            filePath = Path.Combine(targetDir, filename);

            // Ensure unique filename
            filePath = EnsureUniqueFilePath(filePath);
            filename = Path.GetFileName(filePath);

            content = BuildNoteContent(input, context, now);
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, ct);
        }

        _host.Log(PluginLogLevel.Info, $"Saved transcription to {filePath}");
        return new ActionResult(true, $"Saved to {filename}");
#endif
    }

    private static string BuildNoteContent(string input, ActionContext context, DateTime now)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"date: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("source: TypeWhisper");

        if (!string.IsNullOrEmpty(context.AppName))
            sb.AppendLine($"app: \"{EscapeYaml(context.AppName)}\"");

        if (!string.IsNullOrEmpty(context.Language))
            sb.AppendLine($"language: {context.Language}");

        sb.AppendLine("---");
        sb.AppendLine();

        // Body
        sb.AppendLine(input);

        return sb.ToString();
    }

    private static string BuildDailyNoteEntry(string input, ActionContext context, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## {now:HH:mm}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(context.AppName))
            sb.AppendLine($"> Source: {context.AppName}");

        if (!string.IsNullOrEmpty(context.Language))
            sb.AppendLine($"> Language: {context.Language}");

        if (!string.IsNullOrEmpty(context.AppName) || !string.IsNullOrEmpty(context.Language))
            sb.AppendLine();

        sb.AppendLine(input);

        return sb.ToString();
    }

    private static string BuildFilename(string template, ActionContext context, DateTime now)
    {
        var filename = template
            .Replace("{{date}}", now.ToString("yyyy-MM-dd"))
            .Replace("{{time}}", now.ToString("HH-mm-ss"))
            .Replace("{{app}}", context.AppName ?? "Unknown");

        return SanitizeFilename(filename);
    }

    private static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(filename.Length);

        foreach (var c in filename)
        {
            if (Array.IndexOf(invalid, c) >= 0)
                sanitized.Append('_');
            else
                sanitized.Append(c);
        }

        // Trim trailing dots and spaces (Windows restriction)
        var result = sanitized.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "Transcription" : result;
    }

    private static string EnsureUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var dir = Path.GetDirectoryName(filePath)!;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var counter = 2;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt} {counter}{ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Detects installed Obsidian vaults by reading the Obsidian config file.
    /// </summary>
    internal static List<ObsidianVaultInfo> DetectVaults()
    {
        var vaults = new List<ObsidianVaultInfo>();

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var obsidianConfigPath = Path.Combine(appData, "obsidian", "obsidian.json");

            if (!File.Exists(obsidianConfigPath))
                return vaults;

            var json = File.ReadAllText(obsidianConfigPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("vaults", out var vaultsElement))
                return vaults;

            foreach (var vault in vaultsElement.EnumerateObject())
            {
                if (vault.Value.TryGetProperty("path", out var pathElement))
                {
                    var path = pathElement.GetString();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        var name = Path.GetFileName(path);
                        vaults.Add(new ObsidianVaultInfo(name ?? vault.Name, path));
                    }
                }
            }
        }
        catch
        {
            // Silently ignore detection failures
        }

        return vaults;
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => _host = null;

    /// <inheritdoc />
    public IReadOnlyList<PluginTextSetting> TextSettings => _host is null ? [] :
    [
        new("vault-path", "Vault directory", "Enter the absolute path of an existing local vault directory.", _host.GetSetting<string>("vault-path") ?? "", 4096),
        new("subfolder", "Note folder", "Relative folder inside the vault. Links and parent-directory components are unsupported.", _host.GetSetting<string>("subfolder") ?? "TypeWhisper", 1024),
        new("filename-template", "Filename template", "Supports {{date}}, {{time}} and {{app}}. Existing notes are never overwritten.", _host.GetSetting<string>("filename-template") ?? "{{date}} {{time}} Transcription", 512),
        new("note-mode", "Note mode", "Use new-note. Existing daily-note configuration is preserved but cannot run in the portable host.", _host.GetSetting<bool>("daily-note-mode") ? "daily-note" : "new-note", 32)
    ];

    /// <inheritdoc />
    public Task SaveTextSettingAsync(string id, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_host is null) throw new InvalidOperationException("Enable this plugin before configuring it.");
        ArgumentNullException.ThrowIfNull(value);
        var field = TextSettings.FirstOrDefault(field => field.Id == id) ?? throw new ArgumentException("Unknown setting.", nameof(id));
        if (value.Length > field.MaxLength) throw new ArgumentException("The setting is too long.", nameof(value));
        if (id == "note-mode")
        {
            if (value != "new-note") throw new ArgumentException("Only new-note is supported by the portable host.", nameof(value));
            _host.SetSetting("daily-note-mode", false);
        }
        else
        {
            if (id == "vault-path" && (!Path.IsPathFullyQualified(value) || !Directory.Exists(value)))
                throw new ArgumentException("Choose an existing absolute vault directory.", nameof(value));
            if (id == "subfolder")
            {
                if (Path.IsPathRooted(value) || value.Contains(':') || value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part is "." or ".." || part != ObsidianNoteWriter.SafeFilename(part)))
                    throw new ArgumentException("Choose a relative note folder without links or parent components.", nameof(value));
            }
            _host.SetSetting(id, value);
        }
        return Task.CompletedTask;
    }
}

internal sealed record ObsidianVaultInfo(string Name, string Path);
