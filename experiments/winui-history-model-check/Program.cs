using System.Text.Json;
using TypeWhisper.WinUI;

// Isolated model check; no WinUI, network, database, device capture or sync engine.
Console.WriteLine("History model: can type, origin, text and audio vary independently?");
var now = new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
var recording = HistoryEntry.CreateRecorderSample(id, now, now.AddSeconds(84),
    "Meeting", 84, CaptureInputs.Microphone | CaptureInputs.SystemAudio);
Check(recording.RecordId == id && recording.Content.Kind == HistoryEntryKind.Recording,
    "Recorder completion retains session identity and explicit entry kind");
Check(!recording.HasTranscript && recording.AudioAvailability == AudioAvailability.None
    && recording.LocalState.IsSample, "Demo completion does not pretend to contain captured audio or generated text");

var dictation = recording with
{
    Content = recording.Content with
    {
        Kind = HistoryEntryKind.Dictation, LanguageCode = "de",
        Transcript = new HistoryTranscript("hallo welt", "Hallo Welt")
    }
};
dictation.Validate();
Check(dictation.HasTranscript && dictation.AudioAvailability == AudioAvailability.None,
    "Text-only dictation needs no audio descriptor");
var adapter = new Transcript(dictation, "Today · 12:00");
Check(adapter.Duration == "1:24" && adapter.Language == "German" && adapter.WordCount == 2,
    "Existing UI labels are derived from typed data");

var remote = recording with
{
    Content = recording.Content with { Origin = HistoryDevices.Phone, SourceRaw = "iPhone" },
    Audio = new HistoryAudio(now, "audio/mp4", 1234, $"assets/history/{id}/audio.m4a", new string('a', 64)),
    LocalState = new()
};
remote.Validate();
Check(remote.AudioAvailability == AudioAvailability.RemoteOnly && !remote.HasTranscript,
    "iPhone recording can exist with remote audio but no transcript");
var downloaded = remote with { LocalState = new(LocalAudioPath: @"C:\preview-only\audio.m4a") };
downloaded.Validate();
Check(downloaded.RecordId == remote.RecordId && downloaded.AudioAvailability == AudioAvailability.LocalAndRemote,
    "Downloading audio changes local availability, not record identity");
var localAudio = downloaded with { Audio = downloaded.Audio! with { RelativeAssetPath = null, Sha256 = null } };
Check(localAudio.AudioAvailability == AudioAvailability.LocalOnly, "Local audio does not imply it has been synchronized");
Check((localAudio with { LocalState = new() }).AudioAvailability == AudioAvailability.Unavailable,
    "Missing local audio remains distinct from an entry that never had audio");
var dictationWithAudio = dictation with { Audio = remote.Audio };
Check(dictationWithAudio.Content.Kind == HistoryEntryKind.Dictation,
    "An audio attachment does not turn dictation into a recorder session");

var inboxChange = remote with { Inbox = new(now.AddMinutes(5), HistoryInboxState.Completed,
    HistoryCompletionPolicy.Explicit, now.AddMinutes(5)) };
Check(inboxChange.Content.UpdatedAt == remote.Content.UpdatedAt && inboxChange.Audio!.UpdatedAt == remote.Audio!.UpdatedAt,
    "Inbox updates do not overwrite independent content/audio timestamps");
var suppressed = downloaded with { LocalState = downloaded.LocalState with { SuppressedByLocalRetention = true } };
var deletion = new HistoryDeletion(remote.RecordId, now.AddDays(1));
Check(suppressed.LocalState.SuppressedByLocalRetention && deletion.RecordId == remote.RecordId,
    "Local retention and explicit deletion have separate representations");

// This checks safe model separation, NOT compatibility with the Mac wire format.
var json = JsonSerializer.Serialize(suppressed);
Check(!json.Contains("C:") && !json.Contains("LocalState") && !json.Contains("SuppressedByLocalRetention"),
    "Portable model serialization excludes machine-local paths and retention flags");
var restored = JsonSerializer.Deserialize<HistoryEntry>(json)!;
restored.Validate();
Check(restored.RecordId == remote.RecordId && restored.AudioAvailability == AudioAvailability.RemoteOnly,
    "Reading portable data does not claim that audio has already downloaded locally");
var futureSource = remote with { Content = remote.Content with
    { Origin = new("future-device-id", "future-platform", "Renamable device"), SourceRaw = "future-source", Kind = HistoryEntryKind.Unknown } };
futureSource.Validate();
var futureCopy = JsonSerializer.Deserialize<HistoryEntry>(JsonSerializer.Serialize(futureSource))!;
Check(futureCopy.Content.SourceRaw == "future-source" && futureCopy.Content.Origin.Platform == "future-platform",
    "Unknown source/platform values survive without being relabeled as local Windows records");

Reject(recording with { RecordId = Guid.Empty }, "Empty entry identity is rejected");
Reject(recording with { Content = recording.Content with { DurationSeconds = double.NaN } }, "Invalid duration is rejected");
Reject(remote with { Audio = remote.Audio! with { RelativeAssetPath = "assets/history/../outside.m4a" } }, "Parent traversal in remote audio path is rejected");
Reject(remote with { Audio = remote.Audio! with { RelativeAssetPath = @"C:\local\audio.m4a" } }, "Absolute local path cannot become a sync asset reference");
Reject(remote with { Audio = remote.Audio! with { Sha256 = "not-a-digest" } }, "Remote audio requires a valid digest");
var store = new HistoryStore([recording, dictationWithAudio with { RecordId = Guid.NewGuid() },
    remote with { RecordId = Guid.NewGuid() }]);
Check(store.Query().Count == 3, "Shared history includes dictation and recordings");
Check(store.Query(kind: HistoryEntryKind.Dictation).Count == 1,
    "Kind filter does not infer recordings from audio attachments");
Check(store.Query("meeting", HistoryEntryKind.Recording, HistoryDevices.Phone.DeviceId).Count == 1,
    "Text, kind and device filters combine");
Check(store.Query("absent", HistoryEntryKind.Recording).Count == 0, "Combined filters can return no results");
store.Upsert(recording with { Content = recording.Content with { Title = "Renamed session" } });
Check(store.Query().Count == 3 && store.Query("Renamed").Single().RecordId == id,
    "Renaming upserts by identity without creating a duplicate");
store.Upsert(recording with { LocalState = new(SuppressedByLocalRetention: true) });
Check(store.Query().Count == 2, "Locally suppressed entries do not appear in history");
store.Upsert(remote with { RecordId = Guid.NewGuid(), Content = remote.Content with
    { Origin = new("second-phone", "iOS", HistoryDevices.Phone.DeviceName) } });
Check(store.Devices.Count == 3, "Devices with the same display name retain distinct identities");
Console.WriteLine("All history model checks passed. Sync transport and merging remain intentionally unimplemented.");

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine($"PASS: {description}");
}
static void Reject(HistoryEntry entry, string description)
{
    try { entry.Validate(); }
    catch (ArgumentException) { Console.WriteLine($"PASS: {description}"); return; }
    throw new InvalidOperationException(description);
}
