using TypeWhisper.PluginSDK.Models;

public sealed class TranscriptionRequestContextTests
{
    [Fact]
    public async Task ThePurposeFlowsWithTheCallAndScopesRestoreTheOuterValue()
    {
        Assert.Equal(TranscriptionPurpose.Verbatim, TranscriptionRequestContext.Purpose);
        var outer = TranscriptionRequestContext.Begin(TranscriptionPurpose.Dictation);
        await Task.Yield();
        Assert.Equal(TranscriptionPurpose.Dictation, await Task.Run(() => TranscriptionRequestContext.Purpose));
        using (TranscriptionRequestContext.Begin(TranscriptionPurpose.Verbatim))
            Assert.Equal(TranscriptionPurpose.Verbatim, TranscriptionRequestContext.Purpose);
        Assert.Equal(TranscriptionPurpose.Dictation, TranscriptionRequestContext.Purpose);
        outer.Dispose();
        // A second dispose must not restore a value a later scope set.
        using (TranscriptionRequestContext.Begin(TranscriptionPurpose.Dictation))
        {
            outer.Dispose();
            Assert.Equal(TranscriptionPurpose.Dictation, TranscriptionRequestContext.Purpose);
        }
        Assert.Equal(TranscriptionPurpose.Verbatim, TranscriptionRequestContext.Purpose);
    }

    [Fact]
    public async Task APurposeSetInsideACallDoesNotLeakToTheCaller()
    {
        async Task<TranscriptionPurpose> Dictate()
        {
            TranscriptionRequestContext.Begin(TranscriptionPurpose.Dictation);
            await Task.Yield();
            return TranscriptionRequestContext.Purpose;
        }
        Assert.Equal(TranscriptionPurpose.Dictation, await Dictate());
        Assert.Equal(TranscriptionPurpose.Verbatim, TranscriptionRequestContext.Purpose);
    }

    [Fact]
    public void UndefinedPurposesAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptionRequestContext.Begin((TranscriptionPurpose)7));
}
