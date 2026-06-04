using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Script;

/// <summary>
/// Represents script entry data.
/// </summary>
public sealed record ScriptEntry
{
    /// <summary>
    /// Creates or returns a stable identifier.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>
    /// Gets or sets the name value.
    /// </summary>
    public string Name { get; init; } = "";
    /// <summary>
    /// Gets or sets the command value.
    /// </summary>
    public string Command { get; init; } = "";
    /// <summary>
    /// Gets or sets the shell value.
    /// </summary>
    public string Shell { get; init; } = "cmd";
    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Provides script service behavior.
/// </summary>
public sealed class ScriptService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IPluginHostServices _host;
    private readonly string _configPath;

    /// <summary>
    /// Gets the scripts.
    /// </summary>
    public ObservableCollection<ScriptEntry> Scripts { get; } = [];

    /// <summary>
    /// Initializes a new instance of the ScriptService class.
    /// </summary>
    public ScriptService(IPluginHostServices host)
    {
        _host = host;
        _configPath = Path.Combine(host.PluginDataDirectory, "scripts.json");
        Load();
    }

    /// <summary>
    /// Adds script.
    /// </summary>
    public void AddScript(ScriptEntry script)
    {
        Scripts.Add(script);
        Save();
    }

    /// <summary>
    /// Removes script.
    /// </summary>
    public void RemoveScript(Guid id)
    {
        var script = Scripts.FirstOrDefault(s => s.Id == id);
        if (script is not null)
        {
            Scripts.Remove(script);
            Save();
        }
    }

    /// <summary>
    /// Updates script.
    /// </summary>
    public void UpdateScript(ScriptEntry updated)
    {
        for (var i = 0; i < Scripts.Count; i++)
        {
            if (Scripts[i].Id == updated.Id)
            {
                Scripts[i] = updated;
                Save();
                return;
            }
        }
    }

    /// <summary>
    /// Moves up.
    /// </summary>
    public void MoveUp(Guid id)
    {
        var index = IndexOf(id);
        if (index > 0)
        {
            Scripts.Move(index, index - 1);
            Save();
        }
    }

    /// <summary>
    /// Moves down.
    /// </summary>
    public void MoveDown(Guid id)
    {
        var index = IndexOf(id);
        if (index >= 0 && index < Scripts.Count - 1)
        {
            Scripts.Move(index, index + 1);
            Save();
        }
    }

    /// <summary>
    /// Runs scripts asynchronously..
    /// </summary>
    public async Task<string> RunScriptsAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        var current = text;

        foreach (var script in Scripts.ToList())
        {
            if (!script.IsEnabled) continue;

            try
            {
                current = await RunSingleAsync(script, current, context, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _host.Log(PluginLogLevel.Warning, $"Script '{script.Name}' failed: {ex.Message}");
                // Return the text as-is up to this point; don't break the pipeline
            }
        }

        return current;
    }

    private async Task<string> RunSingleAsync(ScriptEntry script, string text, PostProcessingContext context, CancellationToken ct)
    {
        var (fileName, arguments) = script.Shell.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            ? ("powershell.exe", $"-NoProfile -Command {script.Command}")
            : ("cmd.exe", $"/c {script.Command}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.Environment["TYPEWHISPER_APP_NAME"] = context.ActiveAppName ?? "";
        psi.Environment["TYPEWHISPER_LANGUAGE"] = context.SourceLanguage ?? "";
        psi.Environment["TYPEWHISPER_PROFILE"] = context.ProfileName ?? "";

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Write text to stdin and close it so the script knows input is complete
        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — kill the process and return original text
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _host.Log(PluginLogLevel.Warning, $"Script '{script.Name}' timed out after 5 seconds");
            return text;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _host.Log(PluginLogLevel.Warning,
                $"Script '{script.Name}' exited with code {process.ExitCode}: {stderr}");
            return text;
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            _host.Log(PluginLogLevel.Info,
                $"Script '{script.Name}' stderr: {stderr}");
        }

        return stdout;
    }

    private int IndexOf(Guid id)
    {
        for (var i = 0; i < Scripts.Count; i++)
            if (Scripts[i].Id == id) return i;
        return -1;
    }

    private void Load()
    {
        if (!File.Exists(_configPath)) return;

        try
        {
            var json = File.ReadAllText(_configPath);
            var scripts = JsonSerializer.Deserialize<List<ScriptEntry>>(json, s_jsonOptions);
            if (scripts is null) return;

            foreach (var script in scripts)
                Scripts.Add(script);
        }
        catch
        {
            _host.Log(PluginLogLevel.Warning, "Failed to load script configuration");
        }
    }

    /// <summary>
    /// Persists the supplied state to storage.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var json = JsonSerializer.Serialize(Scripts.ToList(), s_jsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _host.Log(PluginLogLevel.Warning, $"Failed to save script configuration: {ex.Message}");
        }
    }
}

/// <summary>
/// Provides script plugin behavior.
/// </summary>
public sealed class ScriptPlugin : IPostProcessorPlugin
{
    private IPluginHostServices? _host;

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.script";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "Script Runner";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";
    /// <summary>
    /// Gets the processor name.
    /// </summary>
    public string ProcessorName => "Script Runner";
    /// <summary>
    /// Gets the priority.
    /// </summary>
    public int Priority => 400;

    /// <summary>
    /// Gets or sets the service value.
    /// </summary>
    public ScriptService? Service { get; private set; }

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Service = new ScriptService(host);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        Service = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        if (Service is null) return text;
        return await Service.RunScriptsAsync(text, context, ct);
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => new ScriptSettingsView(this);

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        Service = null;
    }
}
