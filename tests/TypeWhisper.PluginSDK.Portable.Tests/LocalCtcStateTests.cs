using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class LocalCtcStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctc-state-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Lease : IVocabularyPluginLease
    {
        public IVocabularyRescorerPlugin Plugin { get; } = Mock.Of<IVocabularyRescorerPlugin>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentEnablementOverridesLegacyStandalonePreference(bool enabled)
    {
        var host = new VocabularyHostServices(_root);
        host.SetSetting("Enabled", !enabled);
        var loads = 0;
        Task<IVocabularyPluginLease> Load(IPluginHostServices _) { loads++; return Task.FromResult<IVocabularyPluginLease>(new Lease()); }
        await using var runtime = new LocalCtcVocabulary(_root, Load);
        Assert.Null(await runtime.SetEnabledAsync(enabled));
        Assert.Equal(enabled, runtime.Enabled);
        Assert.Equal(enabled ? 1 : 0, loads);
        Assert.Null(runtime.Error);
        // No independent preference is written by the internal dependency.
        Assert.Equal(!enabled, host.GetSetting<bool>("Enabled"));
        Assert.Null(await runtime.SetEnabledAsync(false));
        Assert.False(runtime.Enabled);
    }

    [Fact]
    public async Task FailedActivationCanBeRetriedAndDependencyCanBeDisabled()
    {
        var fail = true;
        await using var runtime = new LocalCtcVocabulary(_root, _ => fail
            ? throw new IOException("missing model") : Task.FromResult<IVocabularyPluginLease>(new Lease()));
        var packagePath = Path.Combine(_root, LocalCtcVocabulary.PluginId);
        var package = new InstalledPluginPackage(packagePath, new PluginManifest
        { Id = LocalCtcVocabulary.PluginId, Name = "CTC", Version = "1.0", AssemblyName = "ctc.dll", PluginClass = "CTC" }, null);
        var list = new PluginManagementController(_root,
            [new(LocalCtcVocabulary.PluginId, () => runtime.Enabled, () => runtime.Busy, () => runtime.Error, runtime.SetEnabledAsync)],
            () => true, () => Task.FromResult<IReadOnlyList<InstalledPluginPackage>>([package]));
        await list.RefreshAsync();
        Assert.NotNull(await list.SetEnabledAsync(packagePath, true));
        Assert.NotNull(Assert.Single(list.Snapshot()).Error);
        fail = false;
        Assert.Null(await list.SetEnabledAsync(packagePath, true));
        Assert.True(Assert.Single(list.Snapshot()).Enabled);
        Assert.Null(Assert.Single(list.Snapshot()).Error);
        Assert.Null(await list.SetEnabledAsync(packagePath, false));
        Assert.False(runtime.Enabled);
        Assert.False(new VocabularyHostServices(_root).GetSetting<bool>("Enabled"));
    }
}
