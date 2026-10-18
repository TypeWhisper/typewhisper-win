using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Remembers reviewed content independently of application and setup versions.</summary>
public sealed class WhatsNewPreferencesStore
{
    private readonly string _path;
    /// <summary>The highest content version already presented or included in initial setup.</summary>
    public int LastSeenContentVersion { get; private set; }
    /// <summary>A storage failure for diagnostics; optional news must never block startup.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Reads an explicit profile without creating it or replacing invalid data.</summary>
    public WhatsNewPreferencesStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path))
            {
                if (Directory.Exists(path)) throw new IOException("The news preferences path is a directory.");
                return;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid news preferences.");
            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != nameof(LastSeenContentVersion)
                || properties[0].Value.ValueKind != JsonValueKind.Number
                || !properties[0].Value.TryGetInt32(out var version) || version < 0)
                throw new JsonException("Invalid news content version.");
            LastSeenContentVersion = version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = ex; }
    }

    /// <summary>Atomically advances the version; downgrades and failed reads never overwrite it.</summary>
    public void RecordSeen(int contentVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentVersion);
        if (Error is not null || contentVersion <= LastSeenContentVersion) return;
        try
        {
            AtomicFileWriter.WriteAllText(_path, JsonSerializer.Serialize(new { LastSeenContentVersion = contentVersion }));
            LastSeenContentVersion = contentVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = ex; }
    }
}
