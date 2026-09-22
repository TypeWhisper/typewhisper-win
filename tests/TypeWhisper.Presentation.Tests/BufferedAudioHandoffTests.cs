using TypeWhisper.Presentation;
using Xunit;

public sealed class BufferedAudioHandoffTests
{
    [Fact]
    public void PrefixIsCopiedAndDeliveredOnceBeforeLiveAudio()
    {
        var buffer = new BufferedAudioHandoff();
        var received = new List<float>();
        buffer.Append([99]); // No capture outside an explicit recording.
        buffer.Begin();
        float[] first = [1, 2];
        buffer.Append(first);
        first[0] = 99;
        buffer.Append([3]);
        Assert.True(buffer.Attach(received.AddRange));
        buffer.Append([4]);
        Assert.Equal([1f, 2, 3, 4], received);
        buffer.Reset();
        buffer.Append([99]);
        buffer.Begin();
        buffer.Append([5]);
        Assert.True(buffer.Attach(received.AddRange));
        Assert.Equal([1f, 2, 3, 4, 5], received);
    }

    [Fact]
    public void OverflowRequiresFullRecordingFallbackInsteadOfAClippedPrefix()
    {
        var buffer = new BufferedAudioHandoff(3);
        buffer.Begin();
        buffer.Append([1, 2]);
        buffer.Append([3, 4]);
        Assert.False(buffer.Attach(_ => Assert.Fail("A partial prefix must not be sent.")));
        buffer.Begin();
        buffer.Append([5]);
        Assert.True(buffer.Attach(samples => Assert.Equal([5f], samples)));
    }

    [Fact]
    public async Task LiveAudioCannotOvertakeTheBufferedPrefixDuringAttachment()
    {
        var buffer = new BufferedAudioHandoff();
        buffer.Begin();
        buffer.Append([1]);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var received = new List<float>();
        var attach = Task.Run(() => buffer.Attach(samples =>
        {
            if (samples[0] == 1) { draining.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            received.AddRange(samples);
        }));
        try
        {
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var append = Task.Run(() => buffer.Append([2]));
            release.Set();
            Assert.True(await attach);
            await append;
            Assert.Equal([1f, 2], received);
        }
        finally { release.Set(); }
    }
}
