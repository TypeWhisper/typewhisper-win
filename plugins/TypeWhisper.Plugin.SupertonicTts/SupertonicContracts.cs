namespace TypeWhisper.Plugin.SupertonicTts;

internal interface ISupertonicAssetManager
{
    string AssetRoot { get; }
    bool AreAssetsReady { get; }
    Task DownloadMissingAssetsAsync(IProgress<double>? progress, CancellationToken ct);
}

internal interface ISupertonicSynthesizer : IDisposable
{
    SupertonicSynthesisResult Synthesize(SupertonicSynthesisRequest request, CancellationToken ct);
}

internal sealed record SupertonicSynthesisRequest(
    string Text,
    string Language,
    string VoiceStylePath,
    int DenoisingSteps,
    double Speed);

internal sealed record SupertonicSynthesisResult(float[] Samples, int SampleRate);
