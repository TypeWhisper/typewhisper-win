using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>
/// Local speaker diarization with NVIDIA Nemotron 3 Diarization (Streaming Sortformer, up to 8 speakers) through ONNX
/// Runtime on the CPU or, when the user chooses a graphics card, DirectML. Audio never leaves the device.
/// </summary>
public sealed partial class NemotronDiarizationPlugin : IAudioDiarizationProvider
{
    internal const string Id = "com.typewhisper.nemotron-diarization";
    internal const string Engine = "nemotron3-onnx";
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<IReadOnlyList<DirectMlAdapter>> _adapters;
    private readonly Func<NemotronModelAssets, NemotronProfile, DirectMlAdapter?, INemotronStep>? _openStep;
    private readonly IReadOnlyList<NemotronModelFile>? _files;
    private readonly string? _directMlSha256 = NemotronModelAssets.DirectMlModelSha256;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPluginHostServices? _host;
    private NemotronModelAssets? _assets;
    private INemotronStep? _step;
    private (string Profile, DirectMlAdapter? Adapter)? _loadedFor;
    private Timer? _idle;

    /// <summary>Creates the plugin with its own HTTP client for model downloads.</summary>
    public NemotronDiarizationPlugin() : this(new HttpClient { Timeout = TimeSpan.FromHours(2) }, ownsHttp: true) { }

    internal NemotronDiarizationPlugin(HttpClient http, bool ownsHttp = false, Func<IReadOnlyList<DirectMlAdapter>>? adapters = null,
        Func<NemotronModelAssets, NemotronProfile, DirectMlAdapter?, INemotronStep>? openStep = null,
        IReadOnlyList<NemotronModelFile>? files = null, bool checkDirectMlHash = true)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _adapters = adapters ?? DxgiAdapters.List;
        _openStep = openStep;
        _files = files;
        if (!checkDirectMlHash) _directMlSha256 = null;
    }

    /// <inheritdoc />
    public string PluginId => Id;
    /// <inheritdoc />
    public string PluginName => "NVIDIA Nemotron Diarization";
    /// <inheritdoc />
    public string PluginVersion => "1.0.0";
    /// <inheritdoc />
    public string DiarizationProviderId => Id;
    /// <inheritdoc />
    public string DiarizationProviderDisplayName => "NVIDIA Nemotron 3 Diarization";
    /// <inheritdoc />
    public bool AreDiarizationModelsInstalled => _assets?.IsInstalled == true;
    /// <inheritdoc />
    public PluginModelInfo DiarizationModel => new("nemotron-3-diarization", "NVIDIA Nemotron 3 Diarization")
    {
        Publisher = "NVIDIA", SizeDescription = "~380 MB", EstimatedSizeMB = 380
    };

    /// <summary>The device the loaded model runs on, or null while none is loaded.</summary>
    internal string? LoadedDevice => (_step as NemotronOnnxSession)?.Device ?? (_step is null ? null : "test");

    /// <inheritdoc />
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _assets = new NemotronModelAssets(Path.Join(host.PluginAssetDirectory, "Models"), _files, _directMlSha256);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await UnloadDiarizationModelsAsync().ConfigureAwait(false);
        _host = null;
    }

    /// <inheritdoc />
    public async Task PrepareDiarizationModelsAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var assets = RequireAssets();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await assets.DownloadAsync(_http, progress, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
        _host?.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public async Task DeleteDiarizationModelsAsync(CancellationToken cancellationToken)
    {
        var assets = RequireAssets();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseStep();
            assets.Delete();
        }
        finally { _gate.Release(); }
        _host?.NotifyCapabilitiesChanged();
    }

    /// <inheritdoc />
    public async Task UnloadDiarizationModelsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ReleaseStep(); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<PluginDiarizationResult> DiarizeAsync(PluginDiarizationRequest request, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SampleRate != MelFrontend.SampleRate)
            throw new PluginDiarizationException("Nemotron diarization needs 16 kHz audio.", PluginDiarizationFailureKind.UnsupportedAudio);
        if (request.SpeakerCount is not null)
            throw new PluginDiarizationException("Nemotron diarization detects the number of speakers itself.", PluginDiarizationFailureKind.UnsupportedSpeakerCount);
        var assets = RequireAssets();
        if (!assets.IsInstalled)
            throw new PluginDiarizationException("Download the Nemotron diarization model first.", PluginDiarizationFailureKind.ModelsNotInstalled);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _idle?.Change(Timeout.Infinite, Timeout.Infinite);
            var started = Stopwatch.GetTimestamp();
            var (step, profile) = await Task.Run(() => LoadStep(assets), cancellationToken).ConfigureAwait(false);
            var fixedShapes = step is NemotronOnnxSession { FixedShapes: true };
            float[] probabilities;
            try
            {
                probabilities = await Task.Run(() => new NemotronStreamingDiarizer(step, profile, fixedShapes)
                    .Run(request.Samples, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            // A session that failed, for example after its graphics card was removed, is not kept: the next call opens
            // the model again and falls back to the CPU when the graphics card cannot open it.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReleaseStep();
                throw;
            }

            var turns = SpeakerTurns.FromProbabilities(probabilities);
            _host?.Log(PluginLogLevel.Info, $"Diarized {request.DurationSeconds:0.0} s into {turns.Select(turn => turn.SpeakerLabel).Distinct().Count()} speakers "
                + $"in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s ({profile.Id}, {LoadedDevice}).");
            return new PluginDiarizationResult(turns, Engine) { ModelVersion = NemotronModelAssets.Revision[..12] + "/" + profile.Id };
        }
        finally
        {
            _idle ??= new Timer(_ => _ = UnloadDiarizationModelsAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _idle?.Dispose();
        _idle = null;
        ReleaseStep();
        if (_ownsHttp) _http.Dispose();
    }

    // Callers hold _gate. Opens the model for the saved profile and device, again when either changed. A graphics card
    // that cannot open the model falls back to the CPU.
    private (INemotronStep Step, NemotronProfile Profile) LoadStep(NemotronModelAssets assets)
    {
        var profile = SelectedProfile;
        var adapter = SelectedAdapter;
        if (_step is not null && _loadedFor == (profile.Id, adapter)) return (_step, profile);
        ReleaseStep();
        if (_openStep is not null) _step = _openStep(assets, profile, adapter);
        else if (adapter is null) _step = NemotronOnnxSession.Open(assets.CpuModel, assets.DirectMlModel, profile, null);
        else
        {
            try { _step = NemotronOnnxSession.Open(assets.CpuModel, assets.EnsureDirectMlModel(), profile, adapter); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _host?.Log(PluginLogLevel.Warning, "DirectML could not open the diarization model; using the CPU: " + ex.GetType().Name);
                _step = NemotronOnnxSession.Open(assets.CpuModel, assets.DirectMlModel, profile, null);
            }
        }

        _loadedFor = (profile.Id, adapter);
        return (_step, profile);
    }

    private void ReleaseStep()
    {
        (_step as IDisposable)?.Dispose();
        _step = null;
        _loadedFor = null;
    }

    private NemotronModelAssets RequireAssets() =>
        _assets ?? throw new PluginDiarizationException("The plugin is not active.", PluginDiarizationFailureKind.Configuration);
}
