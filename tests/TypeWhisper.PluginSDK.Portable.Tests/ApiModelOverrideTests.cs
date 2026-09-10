using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;

namespace TypeWhisper.WinUI
{
    // Minimal host surface for the linked production request-scope partial.
    internal sealed partial class LocalDictationSession(LocalTranscriptionPlugin models)
    {
        internal LocalTranscriptionPlugin Models => models;
        internal string _providerId = "local";
        internal TestModelRegistry PluginRuntime { get; } = new();
        internal string? ActiveModelId => _providerId == "local" ? Models.ActiveModelId : PluginRuntime.Selected;
        internal bool IsReady => _providerId == "local" ? Models.Ready : PluginRuntime.Selected is not null;
        internal IReadOnlyList<DictationProviderOption> DictationProviders =>
        [new("local", LocalTranscriptionPlugin.PluginId, "Local", Models.Enabled, true, false, Models.ActiveModelId,
            Models.Models.Select(model => new DictationModelOption(model.Model.Id, model.Model.DisplayName, model.Downloaded)).ToArray()),
         new("cloud", "cloud-plugin", "Cloud", true, true, true, PluginRuntime.Selected,
            [new("cloud-a", "Cloud A", true), new("cloud-b", "Cloud B", true)])];
        internal DictationProviderOption? ApiModelProvider(string engine) => DictationProviders.FirstOrDefault(
            provider => provider.Id == engine || provider.PluginId == engine || (provider.Id == "local" && engine == "sherpa-onnx"));
        private static string RegistrySelectionId(string id) => id;
    }
    internal sealed class TestModelRegistry
    {
        internal string? Selected = "cloud-a";
        internal Task<IReadOnlyList<PortableDownloadableModel>> GetModelStatesAsync(string id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PortableDownloadableModel>>([Model("cloud-a"), Model("cloud-b")]);
        private static PortableDownloadableModel Model(string id) => new("cloud-plugin", "cloud", "1", 1, Guid.Empty, id, id, "", false, true, []);
        internal Task SelectModelAsync(PortableDownloadableModel model, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Selected = model.ModelId; return Task.CompletedTask; }
        internal Task DownloadModelAsync(PortableDownloadableModel model, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
        internal Task RefreshCapabilitiesAsync() => Task.CompletedTask;
    }
}

public sealed class ApiModelOverrideTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "api-model-scope-" + Guid.NewGuid());
    private readonly HashSet<string> _downloaded = ["original", "other"];
    private readonly Mock<IPcmTranscriptionEnginePlugin> _engine = new();
    private string? _selected;
    private LocalTranscriptionPlugin _models = null!;
    private LocalDictationSession _session = null!;
    private int _downloads;
    public async Task InitializeAsync()
    {
        _engine.SetupGet(engine => engine.TranscriptionModels).Returns([new("original", "Original"), new("other", "Other"), new("missing", "Missing")]);
        _engine.SetupGet(engine => engine.SelectedModelId).Returns(() => _selected);
        _engine.SetupGet(engine => engine.SupportsModelDownload).Returns(true);
        _engine.Setup(engine => engine.IsModelDownloaded(It.IsAny<string>())).Returns((string model) => _downloaded.Contains(model));
        _engine.Setup(engine => engine.LoadModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string model, CancellationToken ct) => { ct.ThrowIfCancellationRequested(); _selected = model; return Task.CompletedTask; });
        _engine.Setup(engine => engine.SelectModel(It.IsAny<string>())).Callback((string model) => _selected = model);
        _engine.Setup(engine => engine.UnloadModelAsync()).Returns(Task.CompletedTask);
        _engine.Setup(engine => engine.DownloadModelAsync(It.IsAny<string>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((string model, IProgress<double> progress, CancellationToken ct) =>
            { ct.ThrowIfCancellationRequested(); _downloads++; _downloaded.Add(model); return Task.CompletedTask; });
        _models = new(new VocabularyHostServices(_root), () =>
        { _selected = null; return Task.FromResult(new LocalTranscriptionLease(_engine.Object, new Lifetime())); });
        await _models.InitializeAsync();
        _session = new(_models);
    }
    public async Task DisposeAsync() { await _models.DisposeAsync(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Lifetime : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private static ParsedApiTranscription Request(string? model = null, string? engine = null, bool download = false) =>
        new(default, null, null, null, null, "json", model, engine, AwaitDownload: download);

    [Fact]
    public async Task DefaultRequestKeepsTheActiveModel()
    {
        Assert.Null(await _session.BeginApiModelOverrideAsync(Request(), default));
        Assert.Equal("original", _models.ActiveModelId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeRestoresAfterRequestFailureOrCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await using var scope = await _session.BeginApiModelOverrideAsync(Request("other"), cancellation.Token);
            Assert.Equal("other", _models.ActiveModelId);
            if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw new IOException("Transcription failed");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        Assert.Equal("local", _session._providerId);
        Assert.Equal("original", _models.ActiveModelId);
        Assert.Equal("original", new VocabularyHostServices(_root).GetSetting<string>("SelectedModelId"));
    }
    [Fact]
    public async Task AwaitDownloadIsRequiredAndThenRestoresSelection()
    {
        await Assert.ThrowsAsync<LocalApiRequestException>(() => _session.BeginApiModelOverrideAsync(Request("missing"), default));
        Assert.Equal(0, _downloads);
        await using (await _session.BeginApiModelOverrideAsync(Request("missing", download: true), default))
        { Assert.Equal("missing", _models.ActiveModelId); Assert.Equal(1, _downloads); }
        Assert.Equal("original", _models.ActiveModelId);
    }
    [Fact]
    public async Task InvalidModelDoesNotChangeTheSelection()
    {
        await Assert.ThrowsAsync<LocalApiRequestException>(() => _session.BeginApiModelOverrideAsync(Request("unknown"), default));
        Assert.Equal("original", _models.ActiveModelId);
        Assert.Equal("local", _session._providerId);
    }
    [Fact]
    public async Task RegistryOverrideRestoresBothProviderAndItsModel()
    {
        await using (await _session.BeginApiModelOverrideAsync(Request("cloud-b", "cloud"), default))
        { Assert.Equal("cloud", _session._providerId); Assert.Equal("cloud-b", _session.ActiveModelId); }
        Assert.Equal("local", _session._providerId);
        Assert.Equal("cloud-a", _session.PluginRuntime.Selected);
        Assert.Equal("original", _models.ActiveModelId);
    }
    [Fact]
    public async Task UnselectedRegistryProviderIsRejectedWithoutSideEffects()
    {
        _session.PluginRuntime.Selected = null;
        await Assert.ThrowsAsync<LocalApiRequestException>(() => _session.BeginApiModelOverrideAsync(Request("cloud-b", "cloud"), default));
        Assert.Null(_session.PluginRuntime.Selected);
        Assert.Equal("local", _session._providerId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadedLocalModelRestoresItsInternalSelection(bool initiallyUnselected)
    {
        await _models.UnloadAsync(default);
        if (initiallyUnselected) _selected = null;
        var expected = _selected;
        await using (await _session.BeginApiModelOverrideAsync(Request("other"), default))
            Assert.Equal("other", _models.ActiveModelId);
        Assert.Null(_models.ActiveModelId);
        Assert.Equal(expected, _models.SelectedModelId);
        Assert.Equal("original", new VocabularyHostServices(_root).GetSetting<string>("SelectedModelId"));
    }
}
