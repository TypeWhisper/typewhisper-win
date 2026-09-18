using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Plugin.Script;

internal sealed record ScriptConfigurationLoadResult(
    IReadOnlyList<ScriptEntry> Scripts,
    string? Error = null);

internal interface IScriptConfigurationStore
{
    ScriptConfigurationLoadResult Load();
    void Save(IReadOnlyCollection<ScriptEntry> scripts);
}

internal sealed class ScriptConfigurationStore : IScriptConfigurationStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configPath;

    internal ScriptConfigurationStore(string pluginDataDirectory)
    {
        _configPath = Path.Combine(pluginDataDirectory, "scripts.json");
    }

    internal string ConfigPath => _configPath;

    public ScriptConfigurationLoadResult Load()
    {
        if (!File.Exists(_configPath))
            return new ScriptConfigurationLoadResult([]);

        try
        {
            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var scripts = JsonSerializer.Deserialize<List<ScriptEntry?>>(json, s_jsonOptions) ?? [];
            return new ScriptConfigurationLoadResult(scripts.OfType<ScriptEntry>().Select(Normalize).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ScriptConfigurationLoadResult([], ex.Message);
        }
    }

    public void Save(IReadOnlyCollection<ScriptEntry> scripts)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(scripts, s_jsonOptions);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best effort cleanup. The target configuration is never deleted here.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup. The target configuration is never deleted here.
            }
        }
    }

    private static ScriptEntry Normalize(ScriptEntry script) => script with
    {
        Name = script.Name ?? "",
        Command = script.Command ?? "",
        Shell = string.IsNullOrWhiteSpace(script.Shell) ? ScriptShells.CommandPrompt : script.Shell,
        TimeoutSeconds = script.TimeoutSeconds is >= ScriptDefaults.MinimumTimeoutSeconds
            and <= ScriptDefaults.MaximumTimeoutSeconds
                ? script.TimeoutSeconds
                : ScriptDefaults.TimeoutSeconds
    };
}
