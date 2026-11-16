using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModuleAudioDiarizationTests
{
    private static readonly ModuleDiarizationProvider Local = new("local-diarizer", "Local", IsLocal: true, IsReady: true);
    private static readonly ModuleDiarizationProvider Counted = new("counted", "Counted", IsLocal: true, IsReady: true)
        { MinimumSpeakerCount = 2, MaximumSpeakerCount = 4 };

    private sealed class Fake
    {
        internal readonly List<ModuleDiarizationProvider> Providers = [Local, Counted];
        internal Func<PluginDiarizationRequest, Task<PluginDiarizationResult>> Result =
            _ => Task.FromResult(new PluginDiarizationResult([], "fake"));
        internal Func<Task> Prepared = () => Task.CompletedTask;
        internal PluginDiarizationRequest? Request;
        internal string? ProviderId;
        internal Action? Listener;
        internal int Decodes;

        internal ModuleAudioDiarization Create() => new(new(
            () => Providers,
            listener => Listener += listener, listener => Listener -= listener,
            (_, _, _) => Prepared(),
            (id, request, _, _) => { ProviderId = id; Request = request; return Result(request); },
            (_, _) => { Decodes++; return Task.FromResult(new float[48000]); }), action => action(), (stage, _) => Assert.Fail(stage));
    }

    [Fact]
    public void TurnsAreSortedAndSpeakersNamedInTheOrderTheyFirstSpeak()
    {
        var turns = ModuleAudioDiarization.Turns(
        [
            new("spk_7", 4, 6), new("spk_2", 1, 3) { Confidence = 0.8 }, new("spk_7", 0.5, 1.5) { Confidence = 1.7 },
            new("spk_2", 6, 7) { Confidence = double.NaN }
        ], 10);

        Assert.Equal(
        [
            new ModuleSpeakerTurn("S1", 0.5, 1.5, 1), new ModuleSpeakerTurn("S2", 1, 3, 0.8),
            new ModuleSpeakerTurn("S1", 4, 6), new ModuleSpeakerTurn("S2", 6, 7)
        ], turns);
    }

    [Fact]
    public void TurnsWithoutAValidSpanAreDroppedAndOthersStayInsideTheAudio()
    {
        var turns = ModuleAudioDiarization.Turns(
        [
            new("a", -1, 2), new("b", 3, 3), new("c", 5, 4), new("d", double.NaN, 1), new(" ", 1, 2),
            new("e", 8, 12), new("f", 11, 13)
        ], 10);

        Assert.Equal([new ModuleSpeakerTurn("S1", 0, 2), new ModuleSpeakerTurn("S2", 8, 10)], turns);
    }

    [Fact]
    public async Task DiarizeDecodesTheFileAndPassesTheHints()
    {
        var fake = new Fake { Result = request => Task.FromResult(new PluginDiarizationResult(
            [new("x", 2, 3), new("y", 0, 1)], "fake")) };

        var turns = await fake.Create().DiarizeAsync("COUNTED", "C:\\audio.wav", new(SpeakerCount: 3, Language: "de"));

        Assert.Equal("counted", fake.ProviderId);
        Assert.Equal(48000, fake.Request!.Samples.Length);
        Assert.Equal(16000, fake.Request.SampleRate);
        Assert.Equal((3, "de", "C:\\audio.wav"), (fake.Request.SpeakerCount, fake.Request.Language, fake.Request.AudioPath));
        Assert.Equal([new ModuleSpeakerTurn("S1", 0, 1), new ModuleSpeakerTurn("S2", 2, 3)], turns);
    }

    [Fact]
    public async Task UnavailableUnreadyOrOutOfRangeRequestsFailBeforeDecoding()
    {
        var fake = new Fake();
        fake.Providers.Add(new("pending", "Pending", IsLocal: true, IsReady: false) { NeedsModels = true });
        var diarization = fake.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => diarization.DiarizeAsync("missing", "a.wav"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => diarization.DiarizeAsync("pending", "a.wav"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diarization.DiarizeAsync("counted", "a.wav", new(SpeakerCount: 5)));
        // A provider that only detects the count accepts no fixed count.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => diarization.DiarizeAsync("local-diarizer", "a.wav", new(SpeakerCount: 2)));
        Assert.Equal(0, fake.Decodes);
    }

    [Fact]
    public async Task PolicyRefusalsBecomeTheDocumentedModuleException()
    {
        var fake = new Fake
        {
            Result = _ => throw new ManagedPolicyDeniedException("diarization", "Sending audio is turned off."),
            Prepared = () => throw new ManagedPolicyDeniedException("model-download", "Model downloads are turned off.")
        };
        var diarization = fake.Create();

        var refused = await Assert.ThrowsAsync<ModulePolicyRefusedException>(() => diarization.DiarizeAsync("local-diarizer", "a.wav"));
        Assert.Equal("Sending audio is turned off.", refused.Message);
        var download = await Assert.ThrowsAsync<ModulePolicyRefusedException>(() => diarization.PrepareAsync("local-diarizer"));
        Assert.Equal("Model downloads are turned off.", download.Message);
        // Other failures keep their type.
        fake.Result = _ => throw new PluginDiarizationException("Models are missing.", PluginDiarizationFailureKind.ModelsNotInstalled);
        await Assert.ThrowsAsync<PluginDiarizationException>(() => diarization.DiarizeAsync("local-diarizer", "a.wav"));
    }

    [Fact]
    public void ChangesAreDeliveredUntilTheObservationIsDisposed()
    {
        var fake = new Fake();
        var diarization = fake.Create();
        var calls = 0;

        var observation = diarization.ObserveChanges(() => calls++);
        fake.Listener?.Invoke();
        observation.Dispose();
        fake.Listener?.Invoke();

        Assert.Equal(1, calls);
        Assert.Null(fake.Listener);
        Assert.Equal(["local-diarizer", "counted"], diarization.Providers.Select(provider => provider.Id));
    }
}
