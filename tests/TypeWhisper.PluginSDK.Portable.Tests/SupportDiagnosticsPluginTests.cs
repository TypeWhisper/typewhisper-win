using System.Text.Json;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;
using TypeWhisper.WinUI;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

public sealed class SupportDiagnosticsPluginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("typewhisper-support-plugins-").FullName;
    private const string OtherPluginId = "com.typewhisper.unrelated";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InternalCtcFailureIsReportedOnParentPackageAndClearsAfterRecovery(bool parentEnabled)
    {
        var bundles = Path.Join(_root, "bundles");
        Package(bundles, LocalTranscriptionPlugin.PluginId);
        Package(Path.Join(bundles, LocalTranscriptionPlugin.PluginId, "Dependencies"), LocalCtcVocabulary.PluginId, true);
        Package(bundles, OtherPluginId);
        using var http = new HttpClient();
        var store = new PortablePluginStore(Path.Join(_root, "store"), LocalCtcVocabulary.HostVersion, http);
        await store.InitializeAsync(bundles);
        Assert.Equal(2, store.Inventory().Count);
        Assert.False(store.IsInstalled(LocalCtcVocabulary.PluginId));
        await using var registry = new PortablePluginRuntimeRegistry(store, LocalCtcVocabulary.HostVersion,
            id => new VocabularyHostServices(Path.Join(_root, "data", id)), id => id != LocalTranscriptionPlugin.PluginId);
        await registry.InitializeAsync();

        var engine = new Mock<IPcmTranscriptionEnginePlugin>();
        engine.SetupGet(plugin => plugin.TranscriptionModels).Returns([]);
        await using var models = new LocalTranscriptionPlugin(new VocabularyHostServices(Path.Join(_root, "models")),
            () => Task.FromResult(new LocalTranscriptionLease(engine.Object, new Lifetime())));
        await models.SetEnabledAsync(parentEnabled);
        var fail = true;
        await using var vocabulary = new LocalCtcVocabulary(Path.Join(_root, "vocabulary"), _ => fail
            ? throw new IOException("private-error C:\\private-user\\model.onnx")
            : Task.FromResult<IVocabularyPluginLease>(new VocabularyLease()));

        Report Capture()
        {
            var localStates = SupportDiagnosticsExporter.CaptureLocalPluginStates(models, vocabulary, null);
            return new(DateTimeOffset.UtcNow, null, null, null, null, null, null,
                SupportDiagnosticsExporter.Plugins(store, registry, localStates), null, null, null, []);
        }
        Assert.False(Capture().Plugins!.Single(plugin => plugin.Id == LocalTranscriptionPlugin.PluginId).HasError);
        Assert.NotNull(await vocabulary.SetEnabledAsync(true));
        Assert.Null(models.Error);
        var report = Capture();
        var parent = report.Plugins!.Single(plugin => plugin.Id == LocalTranscriptionPlugin.PluginId);
        Assert.True(parent.HasError);
        Assert.Equal(parentEnabled, parent.Enabled);
        Assert.False(report.Plugins!.Single(plugin => plugin.Id == OtherPluginId).HasError);
        Assert.DoesNotContain(report.Plugins!, plugin => plugin.Id == LocalCtcVocabulary.PluginId);
        using var document = JsonDocument.Parse(report.ToJson());
        var exportedParent = document.RootElement.GetProperty("plugins").EnumerateArray()
            .Single(plugin => plugin.GetProperty("id").GetString() == LocalTranscriptionPlugin.PluginId);
        Assert.True(exportedParent.GetProperty("hasError").GetBoolean());
        Assert.DoesNotContain("private", report.ToJson(), StringComparison.OrdinalIgnoreCase);

        fail = false;
        Assert.Null(await vocabulary.SetEnabledAsync(true));
        var recovered = Capture().Plugins!.Single(plugin => plugin.Id == LocalTranscriptionPlugin.PluginId);
        Assert.False(recovered.HasError);
        Assert.Equal(parentEnabled, recovered.Enabled);
    }

    private static void Package(string root, string id, bool internalDependency = false)
    {
        var folder = Path.Join(root, id);
        Directory.CreateDirectory(folder);
        File.Copy(typeof(RuntimeProbePlugin).Assembly.Location, Path.Join(folder, "fixture.dll"));
        File.WriteAllText(Path.Join(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Name = "Diagnostics fixture", Version = "1.0.0", IsLocal = true,
            AssemblyName = "fixture.dll", PluginClass = typeof(RuntimeProbePlugin).FullName!, IsInternalDependency = internalDependency
        }));
    }

    private sealed class Lifetime : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class VocabularyLease : IVocabularyPluginLease
    {
        public IVocabularyRescorerPlugin Plugin { get; } = Mock.Of<IVocabularyRescorerPlugin>();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
