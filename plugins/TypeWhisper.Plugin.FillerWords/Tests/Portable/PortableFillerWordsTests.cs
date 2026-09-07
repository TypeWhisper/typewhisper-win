using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FillerWords.Tests;

public sealed class PortableFillerWordsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filler-portable-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task MultilineSettingsRetainEveryWordAcrossHostRestart(string newline)
    {
        var words = string.Join(newline, "ähm", "sozusagen", "quasi");
        using (var plugin = new FillerWordsPlugin())
        {
            await plugin.ActivateAsync(new VocabularyHostServices(_root));
            await plugin.SaveTextSettingAsync("words", words, default);
        }
        using var restarted = new FillerWordsPlugin();
        await restarted.ActivateAsync(new VocabularyHostServices(_root));
        Assert.Equal(words, Assert.Single(restarted.TextSettings).Value);
        Assert.Equal(3, restarted.Settings!.WordCount);
        Assert.Equal("Das ist gut.", await restarted.ProcessAsync("Das ist ähm sozusagen quasi gut.", new(), default));
    }

    [Fact]
    public async Task SavedTextSettingSurvivesRealHostRestartAndControlsProcessing()
    {
        using (var plugin = new FillerWordsPlugin())
        {
            await plugin.ActivateAsync(new VocabularyHostServices(_root));
            await plugin.SaveTextSettingAsync("words", "basically", default);
        }
        using var restarted = new FillerWordsPlugin();
        await restarted.ActivateAsync(new VocabularyHostServices(_root));
        Assert.Equal("basically", Assert.Single(restarted.TextSettings).Value);
        Assert.Equal("It works, um", await restarted.ProcessAsync("It basically works, um", new(), default));
    }

    [Fact]
    public async Task FailedSavePreservesPublishedAndPersistedSettings()
    {
        var stored = "um";
        var host = new Mock<IPluginHostServices>();
        host.Setup(item => item.GetSetting<string>("words")).Returns(() => stored);
        host.Setup(item => item.SetSetting("words", It.IsAny<string>())).Throws(new IOException("disk unavailable"));
        using var plugin = new FillerWordsPlugin();
        await plugin.ActivateAsync(host.Object);
        await Assert.ThrowsAsync<IOException>(async () => await plugin.SaveTextSettingAsync("words", "basically", default));
        Assert.Equal("um", Assert.Single(plugin.TextSettings).Value);
        Assert.Equal("um", stored);
        Assert.Equal("basically works", await plugin.ProcessAsync("um basically works", new(), default));
    }

    [Fact]
    public async Task CancellationBeforeProcessingOrSaveDoesNotChangeTextSettings()
    {
        using var plugin = new FillerWordsPlugin();
        await plugin.ActivateAsync(new VocabularyHostServices(_root));
        var previous = Assert.Single(plugin.TextSettings).Value;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.ProcessAsync("um yes", new(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await plugin.SaveTextSettingAsync("words", "", canceled.Token));
        Assert.Equal(previous, Assert.Single(plugin.TextSettings).Value);
    }

    [Fact]
    public void RealPackageRequiresExplicitEnablementAndRunsThroughRegistry()
    {
        var context = ExercisePackageWithoutRetainingAsyncState();
        for (var attempt = 0; context.IsAlive && attempt < 30; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "The disabled and disposed package load context must unload before cleanup.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ExercisePackageWithoutRetainingAsyncState() => ExercisePackageAsync().GetAwaiter().GetResult();

    private async Task<WeakReference> ExercisePackageAsync()
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var bundle = Path.Combine(_root, "bundles", manifest.Id); Directory.CreateDirectory(bundle);
        File.Copy(typeof(FillerWordsPlugin).Assembly.Location, Path.Combine(bundle, manifest.AssemblyName));
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), JsonSerializer.Serialize(manifest));
        using var http = new HttpClient();
        var store = new PortablePluginStore(Path.Combine(_root, "store"), new Version(1, 1, 0), http);
        await store.InitializeAsync(Path.Combine(_root, "bundles"));
        await using var registry = new PortablePluginRuntimeRegistry(store, new Version(1, 1, 0),
            id => new VocabularyHostServices(Path.Combine(_root, "data", id)));
        await registry.InitializeAsync();
        Assert.Empty(registry.PostProcessors);
        Assert.Null(await registry.SetEnabledAsync(manifest.Id, true));
        Assert.True(Assert.Single(registry.Snapshot()).HasTextSettings);
        var context = await registry.UseConfigurationAsync(manifest.Id, (plugin, _) =>
            Task.FromResult(new WeakReference(AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly)!)));
        var processor = Assert.Single(registry.PostProcessors);
        Assert.Equal(manifest.Version, processor.Version);
        Assert.Equal("hello", await registry.ProcessTextAsync(processor, "um hello", new()));
        Assert.Null(await registry.SetEnabledAsync(manifest.Id, false));
        Assert.Empty(registry.PostProcessors);
        await registry.DisposeAsync();
        return context;
    }

    public void Dispose()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
