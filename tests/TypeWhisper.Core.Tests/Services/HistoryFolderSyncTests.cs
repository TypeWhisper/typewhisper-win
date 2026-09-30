using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;
using Xunit;

namespace TypeWhisper.Core.Tests.Services;

public sealed class HistoryFolderSyncTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "typewhisper-history-sync-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private const string MacTransport = "8F6B2B8C-1111-4A5B-9C2D-000000000001";
    private const string RecordUuid = "83600000-0000-4000-8000-000000000001";

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private string Package => Path.Combine(_folder, "typewhisper-sync");

    private static TranscriptionRecord Local(string id = "11111111-2222-4333-8444-555555555555", string text = "Hello from Windows") => new()
    {
        Id = id, Timestamp = Now.AddHours(-1), CreatedAt = Now.AddHours(-1), RawText = text, FinalText = text + ".",
        SourceKind = "dictation", AppName = "Notepad", EngineUsed = "sherpa-onnx", ModelUsed = "parakeet-tdt-0.6b", DurationSeconds = 3
    };

    private static HistorySyncResult Sync(string root, string transport, HistorySyncState state, IReadOnlyList<TranscriptionRecord> records, DateTime? now = null) =>
        HistoryFolderSync.Sync(root, transport, state, records, "Test PC", "1.1.0", now ?? Now);

    // A macOS operation file: sorted keys, "key" : value spacing, escaped slashes and an uppercase UUID.
    private void WriteMacOperation(string component, string payload, DateTime updatedAt, string? operationId = null)
    {
        var directory = Path.Combine(Package, "ops", MacTransport);
        Directory.CreateDirectory(directory);
        var id = operationId ?? Guid.NewGuid().ToString().ToUpperInvariant();
        var at = updatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        File.WriteAllText(Path.Combine(directory, $"1790000000000-{id}.json"), $$"""
            {
              "collection" : "history",
              "deviceId" : "{{MacTransport}}",
              "{{component}}" : {{payload}},
              "historyComponent" : "{{(component == "historyContent" ? "content" : "inbox")}}",
              "historyGeneration" : "history-v1",
              "historyPayloadVersion" : 1,
              "itemId" : "history:{{RecordUuid.ToLowerInvariant()}}",
              "kind" : "upsert",
              "operationId" : "{{id}}",
              "schemaVersion" : 1,
              "updatedAt" : "{{at}}"
            }
            """);
    }

    private static string MacContent(DateTime updatedAt, string text = "Diktiert am Mac") => $$"""
        {
          "appDisplayName" : "Notes",
          "createdAt" : "2026-09-30T10:00:00.000Z",
          "detectedLanguage" : "de",
          "durationSeconds" : 4.5,
          "engineDisplayName" : "WhisperKit",
          "finalText" : "{{text}}.",
          "originDeviceID" : "c0ffee00-0000-4000-8000-00000000abcd",
          "originPlatform" : "macOS",
          "processingState" : "ready",
          "rawTranscript" : "{{text}}",
          "recordID" : "{{RecordUuid}}",
          "source" : "mac",
          "updatedAt" : "{{updatedAt:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"
        }
        """;

    private static string MacInbox(DateTime updatedAt, string state) => $$"""
        {
          "completionPolicy" : "explicit",
          "kind" : "watchRecording",
          "recordID" : "{{RecordUuid}}",
          "safeAction" : { "action" : "addToCalendar", "payload" : { "title" : "Standup" }, "version" : 1 },
          "state" : "{{state}}",
          "updatedAt" : "{{updatedAt:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}"
        }
        """;

    [Fact]
    public void PublishesContentAndInboxInTheMacOsFormat()
    {
        var state = new HistorySyncState { Enabled = true, HistoryDeviceId = "aaaaaaaa-0000-4000-8000-000000000001" };
        var result = Sync(_folder, "windows-transport", state, [Local()]);
        Assert.Equal(2, result.OperationsWritten);
        Assert.Null(result.Records);
        var files = Directory.GetFiles(Path.Combine(Package, "ops", "windows-transport"), "*.json");
        var operations = files.Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement).ToArray();
        var content = operations.Single(op => op.GetProperty("historyComponent").GetString() == "content");
        Assert.Equal("history", content.GetProperty("collection").GetString());
        Assert.Equal("history-v1", content.GetProperty("historyGeneration").GetString());
        Assert.Equal("history:11111111-2222-4333-8444-555555555555", content.GetProperty("itemId").GetString());
        var payload = content.GetProperty("historyContent");
        Assert.Equal("11111111-2222-4333-8444-555555555555".ToUpperInvariant(), payload.GetProperty("recordID").GetString());
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", payload.GetProperty("originDeviceID").GetString());
        Assert.Equal("Windows", payload.GetProperty("originPlatform").GetString());
        Assert.Equal("windows", payload.GetProperty("source").GetString());
        Assert.Equal("Hello from Windows.", payload.GetProperty("finalText").GetString());
        Assert.Equal("2026-09-30T11:00:00.000Z", payload.GetProperty("updatedAt").GetString());
        // Missing optional values are omitted, as macOS writes them.
        Assert.False(content.TryGetProperty("deletedAt", out _));
        Assert.False(content.TryGetProperty("historyInbox", out _));
        var inbox = operations.Single(op => op.GetProperty("historyComponent").GetString() == "inbox").GetProperty("historyInbox");
        Assert.Equal("none", inbox.GetProperty("state").GetString());
        Assert.Equal("explicit", inbox.GetProperty("completionPolicy").GetString());
        var device = JsonDocument.Parse(File.ReadAllText(Path.Combine(Package, "devices", "windows-transport.json"))).RootElement;
        Assert.Equal("aaaaaaaa-0000-4000-8000-000000000001", device.GetProperty("historyOriginDeviceID").GetString());
        Assert.Equal("Test PC", device.GetProperty("name").GetString());

        // Nothing changed, so nothing is published again.
        Assert.Equal(0, Sync(_folder, "windows-transport", state, [Local()]).OperationsWritten);
        // An edit publishes only the content.
        var edited = Local() with { FinalText = "Edited.", ContentUpdatedAt = Now };
        Assert.Equal(1, Sync(_folder, "windows-transport", state, [edited]).OperationsWritten);
    }

    [Fact]
    public void ImportsAMacEntryIntoTheInboxWithoutEchoingItBack()
    {
        WriteMacOperation("historyContent", MacContent(Now.AddMinutes(-10)), Now.AddMinutes(-10));
        WriteMacOperation("historyInbox", MacInbox(Now.AddMinutes(-10), "open"), Now.AddMinutes(-10));
        var state = new HistorySyncState { Enabled = true };
        var result = Sync(_folder, "windows-transport", state, []);
        Assert.Equal(2, result.ChangesApplied);
        var record = Assert.Single(result.Records!);
        Assert.Equal(RecordUuid.ToLowerInvariant(), record.Id);
        Assert.Equal(("Diktiert am Mac", "Diktiert am Mac."), (record.RawText, record.FinalText));
        Assert.Equal(("Notes", "de", "WhisperKit"), (record.AppName, record.Language, record.EngineUsed));
        Assert.Equal(("c0ffee00-0000-4000-8000-00000000abcd", "macOS", "mac"), (record.OriginDeviceId, record.OriginPlatform, record.OriginSource));
        Assert.Equal(new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), record.Timestamp);
        Assert.Equal("open", record.InboxState);
        Assert.Equal("watchRecording", record.InboxKind);
        Assert.Equal("addToCalendar", record.InboxSafeAction!.Value.GetProperty("action").GetString());

        // A second pass applies nothing and publishes nothing for the received entry.
        var again = Sync(_folder, "windows-transport", state, result.Records!);
        Assert.Equal((0, 0), (again.ChangesApplied, again.OperationsWritten));
    }

    [Fact]
    public void ReportsOnlyTheVersionsThisPassPublished()
    {
        WriteMacOperation("historyContent", MacContent(Now.AddMinutes(-10)), Now.AddMinutes(-10));
        var state = new HistorySyncState { Enabled = true };
        var result = Sync(_folder, "windows-transport", state, [Local()]);

        // The caller keeps these when it cannot save the merge, so the local entry is not published twice.
        Assert.Equal(["history:11111111-2222-4333-8444-555555555555#content", "history:11111111-2222-4333-8444-555555555555#inbox"],
            result.Published.Keys.Order(StringComparer.Ordinal));
        // The received Mac entry is not saved yet; its version must not look published.
        Assert.True(state.ExportedVersions.ContainsKey($"history:{RecordUuid.ToLowerInvariant()}#content"));
        Assert.DoesNotContain(result.Published.Keys, key => key.Contains(RecordUuid.ToLowerInvariant(), StringComparison.Ordinal));

        var kept = new HistorySyncState { Enabled = true, HistoryDeviceId = state.HistoryDeviceId };
        foreach (var (key, version) in result.Published) kept.ExportedVersions[key] = version;
        Assert.Equal(0, Sync(_folder, "windows-transport", kept, [Local()]).OperationsWritten);
    }

    [Fact]
    public void InboxStampedWithTheCreationTimeIsApplied()
    {
        // macOS gives a new entry's Inbox the same time as its content.
        var created = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        WriteMacOperation("historyContent", MacContent(created), created);
        WriteMacOperation("historyInbox", MacInbox(created, "open"), created);
        var record = Assert.Single(Sync(_folder, "windows-transport", new HistorySyncState { Enabled = true }, []).Records!);
        Assert.Equal("open", record.InboxState);
    }

    [Fact]
    public void NewestInboxChangeWinsInBothDirections()
    {
        WriteMacOperation("historyContent", MacContent(Now.AddMinutes(-10)), Now.AddMinutes(-10));
        WriteMacOperation("historyInbox", MacInbox(Now.AddMinutes(-10), "open"), Now.AddMinutes(-10));
        var state = new HistorySyncState { Enabled = true };
        var records = Sync(_folder, "windows-transport", state, []).Records!;

        // Completing it here publishes the Inbox change, keeping the macOS kind and suggested action.
        var completed = records.Select(record => record with { InboxState = "completed", InboxCompletedAt = Now, InboxUpdatedAt = Now }).ToArray();
        var published = Sync(_folder, "windows-transport", state, completed);
        Assert.Equal(1, published.OperationsWritten);
        var inbox = Directory.GetFiles(Path.Combine(Package, "ops", "windows-transport"))
            .Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement).Single().GetProperty("historyInbox");
        Assert.Equal(("completed", "watchRecording"), (inbox.GetProperty("state").GetString(), inbox.GetProperty("kind").GetString()));
        Assert.True(inbox.TryGetProperty("safeAction", out _));

        // A later reopen on the Mac wins over the earlier completion.
        WriteMacOperation("historyInbox", MacInbox(Now.AddMinutes(5), "open"), Now.AddMinutes(5));
        var reopened = Sync(_folder, "windows-transport", state, completed, Now.AddMinutes(6));
        Assert.Equal("open", Assert.Single(reopened.Records!).InboxState);
    }

    [Fact]
    public void OlderRemoteContentDoesNotReplaceANewerLocalEdit()
    {
        WriteMacOperation("historyContent", MacContent(Now.AddMinutes(-10)), Now.AddMinutes(-10));
        var state = new HistorySyncState { Enabled = true };
        var records = Sync(_folder, "windows-transport", state, []).Records!;
        var edited = records.Select(record => record with { FinalText = "Edited on Windows.", ContentUpdatedAt = Now }).ToArray();
        WriteMacOperation("historyContent", MacContent(Now.AddMinutes(-5), "Late Mac edit"), Now.AddMinutes(-5));
        var result = Sync(_folder, "windows-transport", state, edited);
        Assert.Null(result.Records);
        Assert.Equal(1, result.OperationsWritten);
    }

    [Fact]
    public void ExplicitDeletionsPropagateAndOlderUpsertsDoNotResurrectTheEntry()
    {
        var first = new HistorySyncState { Enabled = true };
        var second = new HistorySyncState { Enabled = true };
        var entry = Local();
        Sync(_folder, "pc-one", first, [entry]);
        var received = Sync(_folder, "pc-two", second, []).Records!;
        Assert.Equal(entry.FinalText, Assert.Single(received).FinalText);

        // Deleting on the first PC reaches the second one.
        first.ExplicitDeletions[HistoryFolderSync.SyncId(entry).ToString()] = Now.AddMinutes(1);
        Assert.Equal(1, Sync(_folder, "pc-one", first, [], Now.AddMinutes(1)).OperationsWritten);
        var afterDelete = Sync(_folder, "pc-two", second, received, Now.AddMinutes(2));
        Assert.Empty(afterDelete.Records!);
        // The original upsert is still in the folder but must not bring the entry back.
        Assert.Null(Sync(_folder, "pc-two", second, afterDelete.Records!, Now.AddMinutes(3)).Records);
    }

    [Fact]
    public void RecordsDevicesForHistoryOriginNames()
    {
        Directory.CreateDirectory(Path.Combine(Package, "devices"));
        File.WriteAllText(Path.Combine(Package, "devices", MacTransport + ".json"), """
            { "appVersion" : "1.4+12@stable", "deviceId" : "8F6B2B8C-1111-4A5B-9C2D-000000000001", "historyOriginDeviceID" : "c0ffee00-0000-4000-8000-00000000abcd",
              "name" : "Marcos MacBook Pro", "platform" : "macOS", "updatedAt" : "2026-09-30T10:00:00.000Z" }
            """);
        var state = new HistorySyncState { Enabled = true };
        Sync(_folder, "windows-transport", state, []);
        Assert.Equal(new HistorySyncDevice("Marcos MacBook Pro", "macOS"), state.Devices["c0ffee00-0000-4000-8000-00000000abcd"]);
    }

    private static byte[] Wave(int seed)
    {
        var data = new byte[1000];
        new Random(seed).NextBytes(data);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header); "WAVE"u8.CopyTo(header.AsSpan(8));
        return [.. header, .. data];
    }

    [Fact]
    public void PublishesAudioOfNewEntriesOnceAsAHashedAsset()
    {
        var wav = Path.Combine(_folder, "local.wav");
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(wav, Wave(1));
        var record = Local() with { AudioFileName = "history-local.wav" };
        var audio = new HistorySyncAudioAccess(_ => wav, (_, _, _) => null, Now.AddHours(-2));
        var state = new HistorySyncState { Enabled = true, AudioEnabled = true };
        var result = HistoryFolderSync.Sync(_folder, "windows-transport", state, [record], "PC", "1", Now, default, audio);
        Assert.Equal(3, result.OperationsWritten);
        var op = Directory.GetFiles(Path.Combine(Package, "ops", "windows-transport")).Select(file => JsonDocument.Parse(File.ReadAllText(file)).RootElement)
            .Single(element => element.GetProperty("historyComponent").GetString() == "audio").GetProperty("historyAudio");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Wave(1))).ToLowerInvariant();
        Assert.Equal($"assets/history/history-v1/11111111-2222-4333-8444-555555555555/{sha}.wav", op.GetProperty("relativeAssetPath").GetString());
        Assert.Equal(1044, op.GetProperty("byteCount").GetInt64());
        Assert.Equal("audio/wav", op.GetProperty("mediaType").GetString());
        Assert.Equal(Wave(1), File.ReadAllBytes(Path.Combine(Package, "assets", "history", "history-v1", "11111111-2222-4333-8444-555555555555", sha + ".wav")));
        Assert.Equal(0, HistoryFolderSync.Sync(_folder, "windows-transport", state, [record], "PC", "1", Now, default, audio).OperationsWritten);
        // Entries older than the moment audio sync was turned on keep their audio on this PC.
        var older = new HistorySyncAudioAccess(_ => wav, (_, _, _) => null, Now);
        var other = new HistorySyncState { Enabled = true, AudioEnabled = true };
        Assert.Equal(2, HistoryFolderSync.Sync(_folder, "pc-two", other, [record], "PC", "1", Now, default, older).OperationsWritten);
    }

    [Fact]
    public void ReceivesAudioOnceItIsCompleteAndMatchesItsHash()
    {
        var created = Now.AddMinutes(-1);
        WriteMacOperation("historyContent", MacContent(created), created);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Wave(2))).ToLowerInvariant();
        var relative = $"assets/history/history-v1/{RecordUuid.ToLowerInvariant()}/{sha}.wav";
        var directory = Path.Combine(Package, "ops", MacTransport);
        File.WriteAllText(Path.Combine(directory, "1790000000001-AUDIO.json"), $$"""
            { "collection" : "history", "deviceId" : "{{MacTransport}}", "historyComponent" : "audio", "historyGeneration" : "history-v1", "historyPayloadVersion" : 1,
              "historyAudio" : { "byteCount" : 1044, "createdAt" : "{{created:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}", "durationSeconds" : 1.5, "mediaType" : "audio\/wav",
                "recordID" : "{{RecordUuid}}", "relativeAssetPath" : "{{relative.Replace("/", "\\/")}}", "sha256" : "{{sha}}", "updatedAt" : "{{created:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}" },
              "itemId" : "history:{{RecordUuid.ToLowerInvariant()}}", "kind" : "upsert", "operationId" : "AUDIO-OP", "schemaVersion" : 1, "updatedAt" : "{{created:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}}" }
            """);
        var imported = new List<string>();
        var audio = new HistorySyncAudioAccess(_ => null, (path, hash, size) => { imported.Add(path); return hash == sha && size == 1044 ? "history-imported.wav" : null; }, Now.AddHours(-1));
        var state = new HistorySyncState { Enabled = true, AudioEnabled = true };

        // The asset is still downloading: the text arrives, the audio waits.
        var first = HistoryFolderSync.Sync(_folder, "windows-transport", state, [], "PC", "1", Now, default, audio);
        Assert.Null(Assert.Single(first.Records!).AudioFileName);
        Assert.Empty(imported);
        Assert.DoesNotContain("AUDIO-OP", state.AppliedOperationIds);

        var asset = Path.Combine(Package, "assets", "history", "history-v1", RecordUuid.ToLowerInvariant(), sha + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllBytes(asset, Wave(2));
        var second = HistoryFolderSync.Sync(_folder, "windows-transport", state, first.Records!, "PC", "1", Now.AddMinutes(1), default, audio);
        Assert.Equal("history-imported.wav", Assert.Single(second.Records!).AudioFileName);
        Assert.Equal(asset, Assert.Single(imported));
        Assert.Contains("AUDIO-OP", state.AppliedOperationIds);
    }

    [Fact]
    public void RejectsAudioPathsOutsideThePackage()
    {
        var bad = new HistorySyncAudio
        {
            RecordId = RecordUuid, RelativeAssetPath = "assets/history/../../secret.wav", Sha256 = new string('a', 64), ByteCount = 10
        };
        Assert.False(bad.IsValid);
        Assert.False((bad with { RelativeAssetPath = "assets/history/x.wav", Sha256 = new string('A', 64) }).IsValid);
        Assert.True((bad with { RelativeAssetPath = "assets/history/history-v1/x/y.wav" }).IsValid);
    }

    [Fact]
    public void InvalidOrForeignOperationsAreIgnored()
    {
        WriteMacOperation("historyContent", MacContent(Now).Replace(RecordUuid, Guid.NewGuid().ToString()), Now);
        var directory = Path.Combine(Package, "ops", MacTransport);
        File.WriteAllText(Path.Combine(directory, "partial.json"), "{ \"collection\" : \"history\", ");
        File.WriteAllText(Path.Combine(directory, "future.json"), """{ "schemaVersion" : 2, "collection" : "history", "historyGeneration" : "history-v2" }""");
        var result = Sync(_folder, "windows-transport", new HistorySyncState { Enabled = true }, []);
        Assert.Equal(0, result.ChangesApplied);
    }

    [Theory]
    [InlineData("dictation", "windows")]
    [InlineData("file", "importedFile")]
    [InlineData("recording", "recorder")]
    public void PublishesSourcesMacOSCanGroup(string kind, string source) =>
        Assert.Equal(source, HistoryFolderSync.Content(Local() with { SourceKind = kind }, Guid.NewGuid(), "history-a").Source);
}
