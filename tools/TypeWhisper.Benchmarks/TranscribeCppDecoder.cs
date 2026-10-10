using System.Globalization;
using TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

/// <summary>One loaded model that turns 16 kHz mono PCM into text.</summary>
internal interface IBenchmarkDecoder : IDisposable
{
    /// <summary>Engine, backend and device as recorded in the summary.</summary>
    string Description { get; }
    string Decode(float[] samples);
    /// <summary>Transcript plus the token times the engine's plugin would hand to the host's CTC rescoring.</summary>
    (string Text, VocabularyTokenTiming[] Timings) DecodeWithTimings(float[] samples);
}

/// <summary>
/// Runs a GGUF model through the same transcribe.cpp bindings as the transcribe.cpp plugin. The native bundle is an
/// extracted transcribe-native release directory (contract.json, transcribe.dll, ggml*.dll).
/// </summary>
internal sealed class TranscribeCppDecoder : IBenchmarkDecoder
{
    internal const string OptionsUsage = "[--native <transcribe-native-directory>] [--backend auto|cpu|vulkan] [--device N]";
    private readonly TranscribeCppSession _session;

    internal TranscribeCppDecoder(string modelPath, int threads, Options options)
    {
        var native = options.NativeDirectory ?? Environment.GetEnvironmentVariable("TYPEWHISPER_TRANSCRIBECPP_NATIVE")
            ?? throw new ArgumentException("A .gguf model needs --native <directory> or TYPEWHISPER_TRANSCRIBECPP_NATIVE.");
        var mask = options.Backend == "cpu" ? TranscribeCppNative.BackendMaskCpu : TranscribeCppNative.BackendMaskCpu | TranscribeCppNative.BackendMaskVulkan;
        TranscribeCppRuntime.Initialize(native, mask);
        var devices = TranscribeCppRuntime.Devices();
        foreach (var device in devices)
            Console.Error.WriteLine($"transcribe.cpp device {device.Index}: {device.Kind} {device.Description} (type {device.DeviceType}, {device.MemoryTotal / (1024 * 1024)} MB)");
        TranscribeCppDevice? selected = options.Device is { } index
            ? devices.FirstOrDefault(device => device.Index == index) ?? throw new ArgumentException("No transcribe.cpp device " + index)
            : options.Backend == "vulkan" ? TranscribeCppRuntime.PreferredVulkanDevice(devices) ?? throw new InvalidOperationException("No Vulkan device.") : null;
        var backend = options.Backend switch
        {
            "cpu" => TranscribeCppNative.BackendCpu,
            "vulkan" => TranscribeCppNative.BackendVulkan,
            _ => TranscribeCppNative.BackendAuto
        };
        _session = TranscribeCppSession.Open(modelPath, backend, selected, threads);
        Description = $"transcribe.cpp {TranscribeCppRuntime.Version()} {_session.Device?.Kind ?? "?"} · {_session.Device?.Description ?? "?"}, {threads} threads";
    }

    public string Description { get; }

    public string Decode(float[] samples) => _session.Transcribe(samples, null, CancellationToken.None);

    public (string Text, VocabularyTokenTiming[] Timings) DecodeWithTimings(float[] samples)
    {
        var result = _session.Transcribe(samples, null, tokens: true, CancellationToken.None);
        // Same conversion as SherpaOnnxPlugin.TokenTimings.
        return (result.Text, TranscriptionTokenTimings.Create(result.Tokens.Select(token => token.Text).ToArray(),
            result.Tokens.Select(token => (float)token.StartSeconds).ToArray(),
            result.Tokens.Select(token => (float)(token.EndSeconds - token.StartSeconds)).ToArray(), samples.Length / 16000d));
    }

    public void Dispose() => _session.Dispose();

    internal sealed class Options
    {
        internal string? NativeDirectory { get; private set; }
        internal string Backend { get; private set; } = "auto";
        internal int? Device { get; private set; }
        private bool _used;

        internal bool TryParse(string name, string value)
        {
            switch (name)
            {
                case "--native": NativeDirectory = Path.GetFullPath(value); break;
                case "--backend":
                    if (value is not ("auto" or "cpu" or "vulkan")) throw new ArgumentException("--backend takes auto, cpu or vulkan.");
                    Backend = value; break;
                case "--device": Device = int.Parse(value, CultureInfo.InvariantCulture); break;
                default: return false;
            }
            _used = true;
            return true;
        }

        internal void EnsureUnused()
        {
            if (_used) throw new ArgumentException("--native, --backend and --device apply only to a .gguf model.");
        }
    }
}
