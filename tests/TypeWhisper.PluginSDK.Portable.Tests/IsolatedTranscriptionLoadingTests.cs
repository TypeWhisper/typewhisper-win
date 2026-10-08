using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class IsolatedTranscriptionLoadingTests
{
    [Fact]
    public async Task ColdRequestsModelChangesAndRestartsLoadBeforeSendingAudio()
    {
        var inner = new Mock<ITranscriptionEnginePlugin>();
        var model = "small";
        inner.SetupGet(e => e.SelectedModelId).Returns(() => model);
        var workers = new List<Worker>();
        await using var engine = new IsolatedTranscriptionEngine(inner.Object, (acceleration, _) =>
        {
            var worker = new Worker(acceleration);
            workers.Add(worker);
            return Task.FromResult<ITranscriptionWorkerConnection>(worker);
        }, (_, _) => { });

        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        model = "large";
        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        Assert.Equal(["load:small:0", "transcribe:small:4", "transcribe:small:4", "load:large:0", "transcribe:large:4"], workers[0].Commands);
        await engine.UnloadModelAsync();
        await engine.TranscribePcmAsync(new float[] { 0 }, null, false, default);
        Assert.Equal(["load:large:0", "transcribe:large:4"], workers[1].Commands);
    }

    [Fact]
    public async Task FailedLoadDoesNotSendAudioOrChangeAcceleration()
    {
        var inner = new Mock<ITranscriptionEnginePlugin>();
        inner.SetupGet(e => e.SelectedModelId).Returns("missing");
        var worker = new Worker(TranscriptionAccelerationPreference.Auto) { LoadError = new("file-not-found", "Model missing") };
        await using var engine = new IsolatedTranscriptionEngine(inner.Object, (_, _) => Task.FromResult<ITranscriptionWorkerConnection>(worker), (_, _) => { });
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.TranscribePcmAsync(new float[] { 0 }, null, false, default));
        Assert.Equal(["load:missing:0"], worker.Commands);
        Assert.False(engine.UsesCpuFallback);
    }

    private sealed class Worker(TranscriptionAccelerationPreference acceleration) : ITranscriptionWorkerConnection
    {
        internal List<string> Commands { get; } = [];
        internal TranscriptionWorkerError? LoadError;
        public TranscriptionAccelerationPreference Acceleration => acceleration;
        public int ProcessId => 123;
        public bool IsAlive { get; private set; } = true;
        public TranscriptionWorkerState? State { get; private set; }
        public Task<TranscriptionWorkerMessage> SendAsync(TranscriptionWorkerMessage request, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Commands.Add($"{request.Command}:{request.ModelId}:{payload.Length}");
            if (request.Command == TranscriptionWorkerCommands.Load)
            {
                if (LoadError is not null) return Task.FromResult(new TranscriptionWorkerMessage { Error = LoadError });
                State = new(request.ModelId, request.ModelId, Acceleration, new(TranscriptionAccelerationBackend.Cpu, "CPU"));
                return Task.FromResult(new TranscriptionWorkerMessage { State = State });
            }
            Assert.Equal(request.ModelId, State?.LoadedModelId);
            return Task.FromResult(new TranscriptionWorkerMessage { State = State, Result = new("text", "en", 0, null, null, null) });
        }
        public ValueTask DisposeAsync() { IsAlive = false; return ValueTask.CompletedTask; }
    }
}
