using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace TypeWhisper.Plugin.Qwen3Local;

internal interface IQwenRecognizer : IDisposable
{
    string Decode(float[] samples, string? language);
}

internal sealed partial class QwenRecognizer : IQwenRecognizer
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
        _recognizer = CreateLoaded(CreateConfig(directory));
    }

    internal static OfflineRecognizerConfig CreateConfig(string directory)
    {
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
        return config;
    }

    // Since sherpa-onnx 2.0 a model that cannot be loaded (for example a damaged file) no longer throws: the
    // recognizer keeps a null native handle and only the first dictation would fail, with a NullReferenceException.
    // The wrapper does not expose that handle, but a stream created from a null recognizer has a null handle too.
    internal static OfflineRecognizer CreateLoaded(OfflineRecognizerConfig config)
    {
        var recognizer = new OfflineRecognizer(config);
        bool loaded;
        using (var probe = recognizer.CreateStream())
            loaded = probe.Handle != IntPtr.Zero;
        if (loaded) return recognizer;
        recognizer.Dispose();
        throw new InvalidOperationException(
            "The Qwen3-ASR model could not be loaded. Its files may be damaged; remove the model and download it again.");
    }

    public string Decode(float[] samples, string? language)
    {
        using var stream = _recognizer.CreateStream();
        if (language is not null) stream.SetOption("language", language);
        stream.AcceptWaveform(QwenAudio.SampleRate, samples);
        _recognizer.Decode(stream);
        return StripLanguageMarkers(stream.Result.Text);
    }

    // sherpa-onnx strips the "language X<asr_text>" prefix for the 0.6B model, but the 1.7B export leaves it
    // in the text of some windows, sometimes mid-window, so any remaining marker is removed here.
    internal static string StripLanguageMarkers(string text) => LanguageMarker().Replace(text, "").Trim();

    [GeneratedRegex(@"(?:language\s+[A-Za-z]+(?:\s[A-Za-z]+)?\s*)?<asr_text>", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageMarker();

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
