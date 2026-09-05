using System.Text.Json;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class VocabularyHostTests
{
    private static VocabularyRescoreRequest Request(string text = "type whisper works") => new(Guid.NewGuid(), text,
        new float[16000], 16000, [new("type whisper", 0, 1)], [new("TypeWhisper")]);

    [Fact]
    public void AppliesOnlyAllowedSpansAndPreservesSurroundingText()
    {
        var request = Request();
        Assert.Equal("TypeWhisper works", VocabularyResultValidator.Apply(request,
            new(request.RecordingId, [new(0, 12, "TypeWhisper", 1)])));
    }

    [Theory]
    [InlineData(-1, 2, "TypeWhisper", 1)]
    [InlineData(0, 0, "TypeWhisper", 1)]
    [InlineData(0, int.MaxValue, "TypeWhisper", 1)]
    [InlineData(int.MaxValue, 2, "TypeWhisper", 1)]
    [InlineData(0, 12, "Unapproved", 1)]
    [InlineData(0, 12, "TypeWhisper", double.NaN)]
    [InlineData(0, 12, "TypeWhisper", double.PositiveInfinity)]
    public void RejectsInvalidReplacements(int start, int length, string term, double score)
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() => VocabularyResultValidator.Apply(request,
            new(request.RecordingId, [new(start, length, term, score)])));
    }

    [Fact]
    public void RejectsStaleRecordingAndOverlappingReplacements()
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() => VocabularyResultValidator.Apply(request, new(Guid.NewGuid(), [])));
        Assert.Throws<InvalidDataException>(() => VocabularyResultValidator.Apply(request,
            new(request.RecordingId, [new(0, 12, "TypeWhisper", 1), new(5, 7, "TypeWhisper", 1)])));
    }

    [Theory]
    [InlineData("😀 text", 0, 1)]
    [InlineData("e\u0301 text", 0, 1)]
    public void RejectsSplitUnicodeGraphemes(string text, int start, int length)
    {
        var request = Request(text);
        Assert.Throws<InvalidDataException>(() => VocabularyResultValidator.Apply(request,
            new(request.RecordingId, [new(start, length, "TypeWhisper", 1)])));
    }

    [Fact]
    public async Task PipelineFallsBackOnPluginFailure()
    {
        var plugin = new Mock<IVocabularyRescorerPlugin>();
        plugin.SetupGet(p => p.IsReady).Returns(true);
        plugin.Setup(p => p.RescoreAsync(It.IsAny<VocabularyRescoreRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("must not leak transcript"));
        var request = Request();
        var result = await Run(plugin.Object, request);
        Assert.Equal(request.Text, result.Text);
        Assert.False(result.Modified);
        Assert.DoesNotContain("must not leak", result.Error);
    }

    [Fact]
    public async Task PipelineCopiesAudioAndRejectsLateCancelledResult()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        var plugin = new Mock<IVocabularyRescorerPlugin>();
        plugin.SetupGet(p => p.IsReady).Returns(true);
        plugin.Setup(p => p.RescoreAsync(It.IsAny<VocabularyRescoreRequest>(), It.IsAny<CancellationToken>()))
            .Returns((VocabularyRescoreRequest r, CancellationToken _) =>
            {
                Assert.False(r.Audio.Equals(request.Audio));
                cancellation.Cancel();
                return Task.FromResult(new VocabularyRescoreResult(r.RecordingId, [new(0, 12, "TypeWhisper", 1)]));
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(plugin.Object, request, cancellation.Token));
    }

    [Fact]
    public async Task MissingTimingSkipsPlugin()
    {
        var plugin = new Mock<IVocabularyRescorerPlugin>(MockBehavior.Strict);
        var result = await Run(plugin.Object, Request() with { TokenTimings = [] });
        Assert.False(result.Modified);
        plugin.VerifyNoOtherCalls();
    }

    [Fact]
    public void LoadsSeparateAssemblyThroughExistingManifestConvention()
    {
        var directory = Path.Combine(Path.GetTempPath(), "typewhisper-plugin-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var source = typeof(ContractProbePlugin).Assembly.Location;
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            var manifest = new PluginManifest { Id = "test.typewhisper.portable-contract", Name = "Fixture", Version = "1.0.0",
                AssemblyName = Path.GetFileName(source), PluginClass = typeof(ContractProbePlugin).FullName! };
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
            var context = LoadAndRelease(directory);
            for (var attempt = 0; context.IsAlive && attempt < 10; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.False(context.IsAlive);
        }
        finally { Directory.Delete(directory, true); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndRelease(string directory)
    {
        var host = new Mock<IPluginHostServices>();
        var package = PortablePluginPackage.LoadAsync(directory, host.Object, new Version(1, 0, 0)).GetAwaiter().GetResult();
        var context = new WeakReference(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(package.Plugin.GetType().Assembly));
        Assert.NotSame(typeof(ContractProbePlugin).Assembly, package.Plugin.GetType().Assembly);
        Assert.True(Assert.IsAssignableFrom<IVocabularyRescorerPlugin>(package.Plugin).IsReady);
        host.Verify(h => h.SetSetting("activations", 1), Times.Once);
        package.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert.False(((IVocabularyRescorerPlugin)package.Plugin).IsReady);
        return context;
    }

    private static Task<VocabularyOutcome> Run(IVocabularyRescorerPlugin plugin, VocabularyRescoreRequest request,
        CancellationToken cancellation = default) => new VocabularyPipeline().RefineAsync(plugin, request.RecordingId,
            request.Text, request.Audio.ToArray(), request.SampleRate, request.TokenTimings, request.Terms, cancellation);
}
