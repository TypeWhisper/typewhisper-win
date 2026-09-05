using System.Text.Json.Serialization;

namespace TypeWhisper.WinUI;

// Local prototype domain model, NOT a new sync wire contract. Content/inbox/audio
// follow the separation in macOS HistoryUserDataSync.swift. EntryKind and
// CaptureInputs need a coordinated cross-platform schema extension before sync.
public enum PrototypeHistoryEntryKind { Unknown, Dictation, Recording, ImportedFile }
public enum PrototypeHistoryProcessingState { Unknown, Importing, Transcribing, Ready, Failed }
public enum PrototypeHistoryInboxState { None, Open, Completed }
public enum PrototypeHistoryCompletionPolicy { OnOpen, Explicit, AfterAction }
public enum PrototypeAudioAvailability { None, LocalOnly, RemoteOnly, LocalAndRemote, Unavailable }
[Flags]
public enum PrototypeCaptureInputs { None = 0, Microphone = 1, SystemAudio = 2 }

// IDs are opaque and independent of display names. Raw platform/source strings
// preserve unfamiliar values from newer clients instead of treating them as global::Windows.
public sealed record PrototypeHistoryOrigin(string DeviceId, string Platform, string? DeviceName);

public static class PrototypeHistoryDevices
{
    // Deliberate fixture identity; production must persist a unique installation ID.
    public static readonly PrototypeHistoryOrigin ThisPc = new("prototype-windows-device", "Windows", "This PC");
    public static readonly PrototypeHistoryOrigin Mac = new("prototype-mac-device", "macOS", "MacBook");
    public static readonly PrototypeHistoryOrigin Phone = new("prototype-phone-device", "iOS", "iPhone");
}

public sealed record PrototypeHistoryTranscript(string RawText, string FinalText, string? RenderedDocument = null);

public sealed record PrototypeHistoryContent(
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PrototypeHistoryOrigin Origin,
    string SourceRaw,
    PrototypeHistoryEntryKind Kind,
    string Title,
    double DurationSeconds,
    PrototypeHistoryProcessingState ProcessingState,
    PrototypeHistoryTranscript? Transcript,
    string? LanguageCode = null,
    PrototypeCaptureInputs CaptureInputs = PrototypeCaptureInputs.None,
    string? EngineName = null,
    string? ModelName = null,
    string? FailureCategory = null,
    string? FailureMessage = null);

public sealed record PrototypeHistoryInbox(
    DateTimeOffset UpdatedAt,
    PrototypeHistoryInboxState State = PrototypeHistoryInboxState.None,
    PrototypeHistoryCompletionPolicy CompletionPolicy = PrototypeHistoryCompletionPolicy.Explicit,
    DateTimeOffset? CompletedAt = null);

// A portable asset reference, never an absolute path on the originating device.
// A null RelativeAssetPath means audio exists but has not been exported for sync.
public sealed record PrototypeHistoryAudio(
    DateTimeOffset UpdatedAt,
    string MediaType,
    long ByteCount,
    string? RelativeAssetPath = null,
    string? Sha256 = null);

public sealed record PrototypeHistoryLocalState(
    string? LocalAudioPath = null,
    bool SuppressedByLocalRetention = false,
    bool IsSample = false);

// Explicit cross-device deletion is distinct from this device's retention cleanup.
// Recording this marker does not itself delete or synchronize anything.
public sealed record PrototypeHistoryDeletion(Guid RecordId, DateTimeOffset DeletedAt);

public sealed record PrototypeHistoryEntry(
    Guid RecordId,
    PrototypeHistoryContent Content,
    PrototypeHistoryInbox Inbox,
    PrototypeHistoryAudio? Audio = null)
{
    [JsonIgnore]
    public PrototypeHistoryLocalState LocalState { get; init; } = new();

    [JsonIgnore]
    public PrototypeAudioAvailability AudioAvailability => Audio is null
        ? PrototypeAudioAvailability.None
        : (!string.IsNullOrWhiteSpace(LocalState.LocalAudioPath), Audio.RelativeAssetPath is not null) switch
        {
            (true, true) => PrototypeAudioAvailability.LocalAndRemote,
            (true, false) => PrototypeAudioAvailability.LocalOnly,
            (false, true) => PrototypeAudioAvailability.RemoteOnly,
            _ => PrototypeAudioAvailability.Unavailable
        };

    [JsonIgnore]
    public bool HasTranscript => Content.Transcript is not null;

    public static PrototypeHistoryEntry CreateRecorderSample(Guid recordId, DateTimeOffset startedAt,
        DateTimeOffset completedAt, string title, double durationSeconds, PrototypeCaptureInputs inputs)
    {
        var entry = new PrototypeHistoryEntry(recordId,
            new PrototypeHistoryContent(startedAt.ToUniversalTime(), completedAt.ToUniversalTime(),
                PrototypeHistoryDevices.ThisPc, "windows", PrototypeHistoryEntryKind.Recording,
                title, durationSeconds, PrototypeHistoryProcessingState.Ready,
                Transcript: null, CaptureInputs: inputs),
            new PrototypeHistoryInbox(completedAt.ToUniversalTime()))
        {
            // The UI's example text is not a generated transcript; no audio exists.
            LocalState = new PrototypeHistoryLocalState(IsSample: true)
        };
        entry.Validate();
        return entry;
    }

    public void Validate()
    {
        if (RecordId == Guid.Empty) throw new ArgumentException("A stable record ID is required.");
        if (string.IsNullOrWhiteSpace(Content.Origin.DeviceId)
            || string.IsNullOrWhiteSpace(Content.Origin.Platform)
            || string.IsNullOrWhiteSpace(Content.SourceRaw))
            throw new ArgumentException("Origin device, platform and source are required.");
        if (!double.IsFinite(Content.DurationSeconds) || Content.DurationSeconds < 0)
            throw new ArgumentException("Duration must be finite and non-negative.");
        if (Content.UpdatedAt < Content.CreatedAt)
            throw new ArgumentException("Content cannot be updated before it was created.");
        if (Audio is null && LocalState.LocalAudioPath is not null)
            throw new ArgumentException("A local audio path needs an audio descriptor.");
        if (Audio is not { } audio) return;
        if (audio.ByteCount < 0 || string.IsNullOrWhiteSpace(audio.MediaType))
            throw new ArgumentException("Audio metadata is invalid.");
        if (audio.RelativeAssetPath is { } path)
        {
            if (!path.StartsWith("assets/history/", StringComparison.Ordinal)
                || path.Contains('\\') || path.Contains(':')
                || path.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException("Audio sync references must be safe relative paths.");
            if (audio.Sha256 is not { Length: 64 } hash
                || !hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new ArgumentException("Remote audio needs a lowercase SHA-256 digest.");
        }
    }
}
