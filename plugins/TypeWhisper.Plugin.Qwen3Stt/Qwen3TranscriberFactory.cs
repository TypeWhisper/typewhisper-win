namespace TypeWhisper.Plugin.Qwen3Stt;

internal interface IQwen3Transcriber : IDisposable
{
    bool UsesDirectMl { get; }

    Qwen3Transcription Transcribe(byte[] wavAudio, string? language, string? prompt, CancellationToken ct);
}

internal interface IQwen3TranscriberFactory
{
    IQwen3Transcriber Load(string modelDirectory, Qwen3ModelDefinition model);

    IQwen3Transcriber LoadCpu(string modelDirectory, Qwen3ModelDefinition model);
}

internal sealed class Qwen3OnnxTranscriberFactory : IQwen3TranscriberFactory
{
    public IQwen3Transcriber Load(string modelDirectory, Qwen3ModelDefinition model) =>
        Qwen3OnnxTranscriber.Load(modelDirectory, model);

    public IQwen3Transcriber LoadCpu(string modelDirectory, Qwen3ModelDefinition model) =>
        Qwen3OnnxTranscriber.LoadCpu(modelDirectory, model);
}
