using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;

namespace TypeWhisper.Plugin.Qwen3Local;

internal interface IQwenRecognizer : IDisposable
{
    string Decode(float[] samples, string? language);
}

internal sealed class QwenRecognizer : IQwenRecognizer
{
    private readonly OfflineRecognizer _recognizer;
    private static readonly object RuntimeLock = new();
    private static bool _registered;

    internal QwenRecognizer(string directory)
    {
        lock (RuntimeLock)
        {
            if (!_registered)
            {
                NativeLibrary.SetDllImportResolver(typeof(OfflineRecognizer).Assembly, ResolveNative);
                _registered = true;
            }
        }
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(directory, "conv_frontend.onnx");
        config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(directory, "encoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(directory, "decoder.int8.onnx");
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(directory, "tokenizer");
        config.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
        config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Tokens = "";
        _recognizer = new OfflineRecognizer(config);
    }

    public string Decode(float[] samples, string? language)
    {
        using var stream = _recognizer.CreateStream();
        if (language is not null) stream.SetOption("language", language);
        stream.AcceptWaveform(QwenAudio.SampleRate, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    public void Dispose() => _recognizer.Dispose();

    private static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (Path.GetFileNameWithoutExtension(name) != "sherpa-onnx-c-api") return IntPtr.Zero;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This Qwen package contains Windows CPU runtimes.");
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64", Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Qwen requires Windows x64 or ARM64.")
        };
        var path = Path.Combine(Path.GetDirectoryName(typeof(QwenRecognizer).Assembly.Location)!, "runtimes", rid, "native", "sherpa-onnx-c-api.dll");
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "qwenort.dll")))
            throw new DllNotFoundException("The Qwen CPU runtime is incomplete. Reinstall the Qwen3 ASR (Local) plugin.");
        return NativeLibrary.Load(path, assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
    }
}
