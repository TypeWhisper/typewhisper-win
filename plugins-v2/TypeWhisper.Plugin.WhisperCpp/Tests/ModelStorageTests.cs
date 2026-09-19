using TypeWhisper.Plugin.WhisperCpp;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Fact]
    public async Task FailedDeselectionKeepsSelectedWeights()
    {
        using var temp = new TempDirectory(); using var plugin = new WhisperCppPlugin();
        var host = new FakePluginHostServices(temp.Path); await plugin.ActivateAsync(host);
        var directory = Path.Join(temp.Path, "Models"); Directory.CreateDirectory(directory);
        var model = Path.Join(directory, "ggml-tiny.bin"); File.WriteAllText(model, "weights");
        plugin.SelectModel("tiny"); host.FailSetting = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.RemoveModelAsync("tiny", default));
        Assert.True(File.Exists(model)); Assert.Equal("tiny", plugin.SelectedModelId);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("tiny", plugin.SelectedModelId); Assert.True(plugin.IsModelDownloaded("tiny"));
    }

    [Fact]
    public async Task NonSeekableDownloadsReportIntermediateProgress()
    {
        using var temp = new TempDirectory(); using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.OpenModelDownloadAsync = (_, _, _) => Task.FromResult<Stream>(new NonSeekableWeights(new byte[200_000]));
        var progress = new CapturedProgress();
        await plugin.DownloadModelAsync("tiny", progress, default);
        Assert.Contains(progress.Values, p => p > 0 && p < 1);
        Assert.Equal(1, progress.Values[^1]); Assert.True(plugin.IsModelDownloaded("tiny"));
    }

    [Fact]
    public void OrphanCleanupPreservesActiveAndUnrelatedFiles()
    {
        using var temp = new TempDirectory();
        var model = Path.Join(temp.Path, "ggml-tiny.bin");
        var stale = model + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var active = model + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var unrelated = Path.Join(temp.Path, "ggml-base.bin." + Guid.NewGuid().ToString("N") + ".tmp");
        var userFile = model + ".notes.tmp";
        File.WriteAllText(stale, "partial"); File.WriteAllText(unrelated, "other"); File.WriteAllText(userFile, "notes");
        using (var download = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            WhisperCppPlugin.RemoveOrphanedModelDownloads(model);
            Assert.False(File.Exists(stale)); Assert.True(File.Exists(active));
            Assert.True(File.Exists(unrelated)); Assert.True(File.Exists(userFile));
        }
        WhisperCppPlugin.RemoveOrphanedModelDownloads(model);
        Assert.False(File.Exists(active));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("broken")]
    public void CorruptedStagedCudaLibraryIsRepaired(string damaged)
    {
        using var temp = new TempDirectory();
        var package = Path.Join(temp.Path, "package"); var runtime = Path.Join(package, "runtimes", "cuda", "win-x64");
        var assets = Path.Join(temp.Path, "assets"); var cache = Path.Join(temp.Path, "cache");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Join(runtime, "whisper.dll"), "native");
        WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
        var staged = Path.Join(cache, "runtimes", "cuda", "win-x64", "whisper.dll");
        File.WriteAllText(staged, damaged);
        WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
        Assert.Equal("native", File.ReadAllText(staged));
    }

    private sealed class NonSeekableWeights(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
    private sealed class CapturedProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }
}
