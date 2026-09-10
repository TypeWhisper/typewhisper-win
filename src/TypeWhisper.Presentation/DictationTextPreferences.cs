using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Immutable text-processing choices captured when recording starts.</summary>
public sealed record DictationTextPreferences
{
    /// <summary>Ordered preferred ISO language codes separated by commas; empty means unrestricted detection.</summary>
    public string PreferredLanguageHints { get; init; } = "";
    /// <summary>Allows quiet captures of at least forty milliseconds to reach final transcription.</summary>
    public bool TranscribeShortQuietClipsAggressively { get; init; }
    /// <summary>Converts recognized spoken numbers using the transcript language.</summary>
    public bool TranscriptionNumberNormalizationEnabled { get; init; } = true;
    /// <summary>Preserves model punctuation on one- and two-word utterances when enabled.</summary>
    public bool ShortUtterancePunctuationEnabled { get; init; } = true;
    /// <summary>Regional spelling applied to English output after corrections.</summary>
    public EnglishOutputVariant EnglishOutputVariant { get; init; } = EnglishOutputVariant.AsTranscribed;
    /// <summary>Preserves German spelling or converts sharp s to Swiss Standard German spelling.</summary>
    public GermanOutputVariant GermanOutputVariant { get; init; } = GermanOutputVariant.AsTranscribed;
    /// <summary>Applies existing application-specific Markdown bullet formatting to known target processes.</summary>
    public bool AppFormattingEnabled { get; init; }
    /// <summary>Spoken formatting overrides keyed by engine, model and language; absent profiles retain engine output.</summary>
    public IReadOnlyList<DictationSpokenFormattingProfile> SpokenFormattingProfiles { get; init; } = [];

    internal bool IsValid => PreferredLanguageHints is not null &&
        (PreferredLanguageHints.Length == 0 || (PreferredLanguageHints.Split(',').Length <= 2 &&
            PreferredLanguageHints.Split(',').All(code => code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z')) &&
            PreferredLanguageHints.Split(',').Distinct(StringComparer.Ordinal).Count() == PreferredLanguageHints.Split(',').Length)) &&
        Enum.IsDefined(EnglishOutputVariant) &&
        (GermanOutputVariant is GermanOutputVariant.AsTranscribed or GermanOutputVariant.Switzerland) &&
        SpokenFormattingProfiles is not null && SpokenFormattingProfiles.All(profile => profile is not null &&
            !string.IsNullOrWhiteSpace(profile.EngineId) && SpokenFormattingLanguageNormalizer.Normalize(profile.LanguageCode) is not null) &&
        SpokenFormattingProfiles.Select(profile => DictationSpokenFormattingProfile.MakeKey(profile.EngineId.Trim(),
            string.IsNullOrWhiteSpace(profile.ModelId) ? null : profile.ModelId.Trim(), SpokenFormattingLanguageNormalizer.Normalize(profile.LanguageCode)!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == SpokenFormattingProfiles.Count;

    internal DictationTextPreferences Snapshot() => this with
    {
        SpokenFormattingProfiles = SpokenFormattingProfiles.Count == 0 ? [] : Array.AsReadOnly(SpokenFormattingProfiles.Select(profile => profile with
        {
            EngineId = profile.EngineId.Trim(), ModelId = string.IsNullOrWhiteSpace(profile.ModelId) ? null : profile.ModelId.Trim(),
            LanguageCode = SpokenFormattingLanguageNormalizer.Normalize(profile.LanguageCode)!
        }).ToArray())
    };
}

/// <summary>Loads and atomically saves text-processing preferences in an explicit profile.</summary>
public sealed class DictationTextPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _path;
    /// <summary>The loaded or last successfully saved immutable preferences.</summary>
    public DictationTextPreferences Current { get; private set; } = new();
    /// <summary>A user-facing load or save failure, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Loads without writing; unreadable or invalid settings use the documented defaults.</summary>
    public DictationTextPreferencesStore(string path)
    {
        _path = path;
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || new[]
                {
                    nameof(DictationTextPreferences.TranscriptionNumberNormalizationEnabled),
                    nameof(DictationTextPreferences.ShortUtterancePunctuationEnabled),
                    nameof(DictationTextPreferences.EnglishOutputVariant),
                    nameof(DictationTextPreferences.GermanOutputVariant)
                }.Any(key => !root.TryGetProperty(key, out _)))
                throw new JsonException("Incomplete text preferences.");
            var loaded = JsonSerializer.Deserialize<DictationTextPreferences>(json, JsonOptions);
            if (loaded is null || !loaded.IsValid) throw new JsonException("Invalid text preferences.");
            Current = loaded.Snapshot();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Error = "Text preferences could not be loaded. Default text processing is active. Save your choices to restore them.";
        }
    }

    /// <summary>Persists valid choices; failed writes preserve the previous preferences.</summary>
    public string? Save(DictationTextPreferences next)
    {
        if (!next.IsValid) return Error = "Choose supported text-processing options and unique formatting profiles.";
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            next = next.Snapshot();
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            Current = next;
            return Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error = "Text preferences could not be saved. Your previous choices still apply.";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
