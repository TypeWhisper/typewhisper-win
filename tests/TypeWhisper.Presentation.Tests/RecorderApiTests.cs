using System.Text.Json;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.WinUI
{
    // Headless host for the production API partial; capture/save transitions use the real controller.
    public sealed partial class RecorderView
    {
        private readonly RecorderController _recorder;
        private readonly RecorderPreferencesStore? _recorderPreferences = null;
        private readonly bool _libraryClosing = false;
        internal bool Microphone { get; private set; }
        internal bool SystemAudio { get; private set; }
        internal RecorderView(Func<Task<float[]>>? stop = null, Func<float[], Task<string>>? save = null)
        {
            _recorder = new(() => new Reservation(), (mic, system) =>
            { Microphone = mic; SystemAudio = system; return Task.CompletedTask; },
                stop ?? (() => Task.FromResult(new float[1600])), save ?? (_ => Task.FromResult("C:/recordings/test.wav")));
            _recorder.Changed += Refresh;
        }
        private sealed class Reservation : IDisposable { public void Dispose() { } }
        private Task StartRecordingAsync(RecorderPreferences preferences)
        {
            if (_apiRecorderSession is { Started: true }) _apiRecorderSession = null;
            return _recorder.StartAsync(preferences.MicrophoneEnabled, preferences.SystemAudioEnabled);
        }
        private async Task StopAsync() { try { await _recorder.StopAndSaveAsync(); } catch (IOException) { } Refresh(); }
        private void Refresh() => UpdateApiRecorderSession();
        internal Task StartFromUiAsync() => StartRecordingAsync(new());
        internal Task StopFromUiAsync() => StopAsync();
    }
}

namespace TypeWhisper.Presentation.Tests
{
    public sealed class RecorderApiTests
    {
        private static LocalApiRequest Request(string path, string method = "POST", params (string Key, string Value)[] query) =>
            new(method, "/v1/recorder/" + path, [], null, query.ToDictionary(p => p.Key, p => (string?)p.Value));
        private static async Task<JsonElement> Send(TypeWhisper.WinUI.RecorderView view, LocalApiRequest request, int code = 200)
        {
            var response = await view.HandleApiAsync(request, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(code, response.StatusCode);
            return JsonSerializer.Deserialize<JsonElement>(response.Body);
        }

        [Fact]
        public async Task StopReturnsBeforeCaptureFinishesAndPollingPreservesSessionId()
        {
            var capture = new TaskCompletionSource<float[]>();
            var view = new TypeWhisper.WinUI.RecorderView(() => capture.Task);
            var started = await Send(view, Request("start", "POST", ("mic", "false"), ("system_audio", "1")));
            var id = started.GetProperty("id").GetString()!;
            Assert.False(view.Microphone); Assert.True(view.SystemAudio);
            await Send(view, Request("start"), 409);
            var stopped = await Send(view, Request("stop"));
            Assert.Equal(id, stopped.GetProperty("id").GetString());
            Assert.Equal("finalizing", stopped.GetProperty("status").GetString());
            var pending = await Send(view, Request("session", "GET", ("id", id)));
            Assert.Equal("finalizing", pending.GetProperty("status").GetString());
            Assert.False(pending.TryGetProperty("output_file", out _));
            capture.SetResult(new float[1600]);
            await view.StopFromUiAsync(); // Drains the already-running controller transition.
            var completed = await Send(view, Request("session", "GET", ("id", id)));
            Assert.Equal("completed", completed.GetProperty("status").GetString());
            Assert.Equal("C:/recordings/test.wav", completed.GetProperty("output_file").GetString());
        }

        [Fact]
        public async Task CompletedApiResultSurvivesLaterUiRecording()
        {
            var file = "C:/recordings/first.wav";
            var view = new TypeWhisper.WinUI.RecorderView(save: _ => Task.FromResult(file));
            var started = await Send(view, Request("start"));
            var id = started.GetProperty("id").GetString()!;
            await view.StopFromUiAsync();
            file = "C:/recordings/second.wav";
            await view.StartFromUiAsync();
            await view.StopFromUiAsync();
            var first = await Send(view, Request("session", "GET", ("id", id)));
            Assert.Equal("C:/recordings/first.wav", first.GetProperty("output_file").GetString());
        }

        [Fact]
        public async Task FailedSaveIsPollableWithoutLeakingExceptionDetails()
        {
            var view = new TypeWhisper.WinUI.RecorderView(save: _ => throw new IOException("private disk details"));
            var started = await Send(view, Request("start"));
            var id = started.GetProperty("id").GetString()!;
            await Send(view, Request("stop"));
            await view.StopFromUiAsync();
            var failed = await Send(view, Request("session", "GET", ("id", id)));
            Assert.Equal("failed", failed.GetProperty("status").GetString());
            Assert.DoesNotContain("private", failed.GetRawText());
            await Send(view, Request("start"), 409);
        }

        [Fact]
        public async Task ValidationAndCancellationPrecedeCapture()
        {
            var view = new TypeWhisper.WinUI.RecorderView();
            await Send(view, Request("start", "GET"), 405);
            await Send(view, Request("start", "POST", ("mic", "false"), ("system_audio", "0")), 400);
            await Send(view, Request("start", "POST", ("mic", "yes")), 400);
            await Send(view, Request("start", "POST", ("unknown", "true")), 400);
            await Send(view, Request("session", "GET", ("id", "invalid")), 400);
            await Send(view, Request("session", "GET", ("id", Guid.NewGuid().ToString())), 404);
            await Send(view, Request("stop"), 409);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.HandleApiAsync(Request("start"), new CancellationToken(true)));
            var status = await Send(view, Request("status", "GET"));
            Assert.False(status.GetProperty("recording").GetBoolean());
        }
    }
}
