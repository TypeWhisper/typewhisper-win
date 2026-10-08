using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiAudioSettingsTests : IDisposable
{
    private static readonly MicrophonePriorityItem QuadCast = new("{0.0.1.00000000}.{quadcast}", "Microphone (HyperX QuadCast 2)");
    private static readonly MicrophonePriorityItem Cloud = new("{0.0.1.00000000}.{cloud-iii}", "Microphone (HyperX Cloud III)");
    private static readonly LocalApiAudioInput Sonar = new("{0.0.1.00000000}.{sonar}", "SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", true);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-api-audio-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAudioTarget _target;

    public LocalApiAudioSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _target = new FakeAudioTarget(_directory)
        {
            InputDevices = [Sonar, new(QuadCast.Id, QuadCast.Name, false)]
        };
        _target.Seed([QuadCast, Cloud], new DictationAudioPreferences
        {
            AudioDuckingEnabled = true, AudioDuckingLevel = .37f, PauseMediaDuringRecording = false, SoundFeedbackEnabled = true,
            SilenceAutoStopEnabled = true, SilenceAutoStopSeconds = 15, OutputDeviceId = "{0.0.0.00000000}.{headset}", WhisperModeEnabled = true
        });
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private LocalApiResponse Call(string method, string? body = null, string contentType = "application/json",
        IReadOnlyDictionary<string, string?>? query = null, string path = LocalApiAudioSettings.Path) =>
        new LocalApiAudioSettings(_target).Handle(new(method, path, Encoding.UTF8.GetBytes(body ?? ""),
            body is null ? null : contentType, query ?? new Dictionary<string, string?>()))!;

    private static JsonElement Json(LocalApiResponse response) => JsonDocument.Parse(response.Body).RootElement;

    [Fact]
    public void GetReturnsDevicesPriorityActiveInputAndPreferences()
    {
        _target.ActiveInput = QuadCast;
        var response = Call("GET");
        Assert.Equal(200, response.StatusCode);
        var root = Json(response);
        Assert.Equal(new[] { "input_devices", "input_priority", "active_input", "audio_ducking_enabled", "audio_ducking_level",
            "pause_media_during_recording", "sound_feedback_enabled" }, root.EnumerateObject().Select(field => field.Name));
        var device = root.GetProperty("input_devices")[0];
        Assert.Equal(Sonar.Id, device.GetProperty("id").GetString());
        Assert.Equal(Sonar.Name, device.GetProperty("name").GetString());
        Assert.True(device.GetProperty("is_system_default").GetBoolean());
        Assert.Equal(new[] { QuadCast.Id, Cloud.Id }, root.GetProperty("input_priority").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(new[] { "id", "name" }, root.GetProperty("input_priority")[1].EnumerateObject().Select(field => field.Name));
        Assert.Equal(QuadCast.Name, root.GetProperty("active_input").GetProperty("name").GetString());
        Assert.True(root.GetProperty("audio_ducking_enabled").GetBoolean());
        // Written as a float, so it reads as the configured 0.37 rather than 0.3700000047683716.
        Assert.Equal("0.37", root.GetProperty("audio_ducking_level").GetRawText());
        Assert.False(root.GetProperty("pause_media_during_recording").GetBoolean());
        Assert.True(root.GetProperty("sound_feedback_enabled").GetBoolean());
    }

    [Fact]
    public void GetReportsMissingActiveInputAsNull()
    {
        _target.ActiveInput = null;
        Assert.Equal(JsonValueKind.Null, Json(Call("GET")).GetProperty("active_input").ValueKind);
    }

    [Fact]
    public void PatchAppliesSubsetThroughSessionAndReturnsFullState()
    {
        var response = Call("PATCH", $$"""{"input_priority":[{"id":"{{Sonar.Id}}","name":"{{Sonar.Name}}"}],"audio_ducking_enabled":false}""");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { new MicrophonePriorityItem(Sonar.Id, Sonar.Name) }, _target.InputPriority);
        Assert.False(_target.Preferences.AudioDuckingEnabled);
        // Fields not in the request keep their values.
        Assert.Equal(.37f, _target.Preferences.AudioDuckingLevel);
        Assert.Equal(15, _target.Preferences.SilenceAutoStopSeconds);
        Assert.Equal(1, _target.PriorityWrites);
        Assert.Equal(1, _target.PreferenceWrites);
        var root = Json(response);
        Assert.Equal(Sonar.Id, root.GetProperty("input_priority")[0].GetProperty("id").GetString());
        Assert.False(root.GetProperty("audio_ducking_enabled").GetBoolean());
        Assert.Equal(2, root.GetProperty("input_devices").GetArrayLength());
    }

    [Fact]
    public void EmptyPriorityMeansWindowsDefaultAndUnchangedValuesAreNotRewritten()
    {
        Assert.Equal(200, Call("PATCH", """{"input_priority":[]}""").StatusCode);
        Assert.Empty(_target.InputPriority);
        Assert.Equal("[]", File.ReadAllText(_target.PriorityPath));
        Assert.Equal(0, _target.PreferenceWrites);

        Assert.Equal(200, Call("PATCH", """{"input_priority":[],"sound_feedback_enabled":true}""").StatusCode);
        Assert.Equal(200, Call("PATCH", "{}").StatusCode);
        Assert.Equal(1, _target.PriorityWrites);
        Assert.Equal(0, _target.PreferenceWrites);
    }

    [Fact]
    public void RestoringSavedGetStateIsLossless()
    {
        var priorityBefore = File.ReadAllBytes(_target.PriorityPath);
        var preferencesBefore = File.ReadAllBytes(_target.PreferencesPath);
        var saved = JsonNode.Parse(Call("GET").Body)!.AsObject();

        // The demo runner's temporary change: Sonar first, ducking off.
        var temporary = Call("PATCH", $$"""
            {"input_priority":[{"id":"{{Sonar.Id}}","name":"{{Sonar.Name}}"},{"id":"{{QuadCast.Id}}","name":"{{QuadCast.Name}}"}],
             "audio_ducking_enabled":false,"audio_ducking_level":0.1,"pause_media_during_recording":true,"sound_feedback_enabled":false}
            """);
        Assert.Equal(200, temporary.StatusCode);
        Assert.NotEqual(priorityBefore, File.ReadAllBytes(_target.PriorityPath));
        Assert.NotEqual(preferencesBefore, File.ReadAllBytes(_target.PreferencesPath));

        saved.Remove("input_devices");
        saved.Remove("active_input");
        Assert.Equal(200, Call("PATCH", saved.ToJsonString()).StatusCode);
        Assert.Equal(priorityBefore, File.ReadAllBytes(_target.PriorityPath));
        Assert.Equal(preferencesBefore, File.ReadAllBytes(_target.PreferencesPath));
        Assert.Equal(.37f, _target.Preferences.AudioDuckingLevel);
    }

    [Theory]
    [InlineData("""{"input_devices":[]}""")]
    [InlineData("""{"active_input":null}""")]
    [InlineData("""{"whisper_mode_enabled":true}""")]
    [InlineData("""{"output_device_id":"x"}""")]
    [InlineData("""{"audio_ducking_enabled":false,"audio_ducking_enabled":true}""")]
    [InlineData("""{"audio_ducking_enabled":"false"}""")]
    [InlineData("""{"audio_ducking_enabled":null}""")]
    [InlineData("""{"sound_feedback_enabled":0}""")]
    [InlineData("""{"pause_media_during_recording":"yes"}""")]
    [InlineData("""{"audio_ducking_level":1.01}""")]
    [InlineData("""{"audio_ducking_level":-0.1}""")]
    [InlineData("""{"audio_ducking_level":"0.5"}""")]
    [InlineData("""{"audio_ducking_level":1e999}""")]
    [InlineData("""{"input_priority":null}""")]
    [InlineData("""{"input_priority":{"id":"a","name":"A"}}""")]
    [InlineData("""{"input_priority":["a"]}""")]
    [InlineData("""{"input_priority":[{"id":"a"}]}""")]
    [InlineData("""{"input_priority":[{"name":"A"}]}""")]
    [InlineData("""{"input_priority":[{"id":" ","name":"A"}]}""")]
    [InlineData("""{"input_priority":[{"id":"a","name":null}]}""")]
    [InlineData("""{"input_priority":[{"id":"a","name":"A","is_system_default":true}]}""")]
    [InlineData("""{"input_priority":[{"id":"a","id":"b","name":"A"}]}""")]
    [InlineData("""{"input_priority":[{"id":"a","name":"A"},{"id":"A","name":"Other"}]}""")]
    [InlineData("""[]""")]
    [InlineData("""{"audio_ducking_enabled":false""")]
    [InlineData("""{"sound_feedback_enabled":false,"unknown":1}""")]
    public void InvalidPatchIsRejectedWithoutChangingAnything(string body)
    {
        var priority = File.ReadAllBytes(_target.PriorityPath);
        var preferences = File.ReadAllBytes(_target.PreferencesPath);
        var response = Call("PATCH", body);
        Assert.Equal(400, response.StatusCode);
        Assert.Equal("bad_request", Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _target.PriorityWrites + _target.PreferenceWrites);
        Assert.Equal(priority, File.ReadAllBytes(_target.PriorityPath));
        Assert.Equal(preferences, File.ReadAllBytes(_target.PreferencesPath));
    }

    [Fact]
    public void PriorityLongerThanLimitIsRejected()
    {
        var items = string.Join(",", Enumerable.Range(0, 101).Select(i => $$"""{"id":"d{{i}}","name":"D{{i}}"}"""));
        Assert.Equal(400, Call("PATCH", $$"""{"input_priority":[{{items}}]}""").StatusCode);
    }

    [Theory]
    [InlineData("PATCH", "", "application/json")]
    [InlineData("PATCH", """{"sound_feedback_enabled":false}""", "text/plain")]
    [InlineData("GET", "{}", "application/json")]
    public void RequestShapeErrorsReturn400(string method, string body, string contentType)
    {
        var response = new LocalApiAudioSettings(_target).Handle(new(method, LocalApiAudioSettings.Path, Encoding.UTF8.GetBytes(body),
            contentType, new Dictionary<string, string?>()))!;
        Assert.Equal(400, response.StatusCode);
        Assert.Equal(0, _target.PriorityWrites + _target.PreferenceWrites);
    }

    [Fact]
    public void QueryParametersAndOtherMethodsAreRejected()
    {
        Assert.Equal(400, Call("GET", query: new Dictionary<string, string?> { ["id"] = "1" }).StatusCode);
        Assert.Equal(405, Call("PUT", """{"sound_feedback_enabled":false}""").StatusCode);
        Assert.Equal(405, Call("POST", "{}").StatusCode);
        Assert.Null(Call("GET", path: "/v1/settings/export"));
    }

    [Fact]
    public void PatchDuringRecordingOrProcessingReturns409WithoutChanges()
    {
        _target.CanChange = false;
        var response = Call("PATCH", """{"input_priority":[],"audio_ducking_enabled":false}""");
        Assert.Equal(409, response.StatusCode);
        Assert.Equal("conflict", Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _target.PriorityWrites + _target.PreferenceWrites);
        Assert.Equal(new[] { QuadCast, Cloud }, _target.InputPriority);
        // Reading stays available while busy.
        Assert.Equal(200, Call("GET").StatusCode);
    }

    [Fact]
    public void SessionRejectingABusyChangeReturns409()
    {
        _target.FailPriority = () => { _target.CanChange = false; return "Finish the current recording before changing microphones."; };
        Assert.Equal(409, Call("PATCH", """{"input_priority":[]}""").StatusCode);
        Assert.Equal(new[] { QuadCast, Cloud }, _target.InputPriority);
    }

    [Fact]
    public void PreferenceSaveFailureRestoresPreviousPriority()
    {
        var priority = File.ReadAllBytes(_target.PriorityPath);
        _target.FailPreferences = () => "Access denied";
        var response = Call("PATCH", """{"input_priority":[],"audio_ducking_enabled":false}""");
        Assert.Equal(500, response.StatusCode);
        Assert.Contains("Access denied", Json(response).GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(new[] { QuadCast, Cloud }, _target.InputPriority);
        Assert.Equal(priority, File.ReadAllBytes(_target.PriorityPath));
        Assert.True(_target.Preferences.AudioDuckingEnabled);
    }

    [Fact]
    public void CatalogRegistersGetAndPatch()
    {
        Assert.True(LocalApiRouteCatalog.Contains("GET", LocalApiAudioSettings.Path));
        Assert.True(LocalApiRouteCatalog.Contains("PATCH", LocalApiAudioSettings.Path));
        Assert.False(LocalApiRouteCatalog.Contains("PUT", LocalApiAudioSettings.Path));
    }

    // Persists like LocalDictationSession.SetMicrophonePriority and SaveAudioPreferences.
    private sealed class FakeAudioTarget(string directory) : ILocalApiAudioTarget
    {
        internal string PriorityPath => Path.Combine(directory, "microphone.json");
        internal string PreferencesPath => Path.Combine(directory, "audio.json");
        internal int PriorityWrites { get; private set; }
        internal int PreferenceWrites { get; private set; }
        internal Func<string?>? FailPriority { get; set; }
        internal Func<string?>? FailPreferences { get; set; }
        public bool CanChange { get; set; } = true;
        public IReadOnlyList<LocalApiAudioInput> InputDevices { get; init; } = [];
        public IReadOnlyList<MicrophonePriorityItem> InputPriority { get; private set; } = [];
        public MicrophonePriorityItem? ActiveInput { get; set; }
        public DictationAudioPreferences Preferences { get; private set; } = new();

        internal void Seed(IReadOnlyList<MicrophonePriorityItem> priority, DictationAudioPreferences preferences)
        {
            File.WriteAllText(PriorityPath, JsonSerializer.Serialize(priority.ToList()));
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(preferences.Validated()));
            InputPriority = priority;
            Preferences = preferences.Validated();
        }

        public string? SetInputPriority(IReadOnlyList<MicrophonePriorityItem> priority)
        {
            if (FailPriority?.Invoke() is { } error) return error;
            var selected = priority.DistinctBy(item => item.Id).ToList();
            File.WriteAllText(PriorityPath, JsonSerializer.Serialize(selected));
            InputPriority = selected;
            PriorityWrites++;
            return null;
        }

        public string? SavePreferences(DictationAudioPreferences preferences)
        {
            if (FailPreferences?.Invoke() is { } error) return error;
            preferences = preferences.Validated();
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(preferences));
            Preferences = preferences;
            PreferenceWrites++;
            return null;
        }
    }
}
