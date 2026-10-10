using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
/// P/Invoke surface of transcribe.cpp's generic C API (include/transcribe.h), pinned to one release.
/// Only the offline subset the Parakeet GPU path needs is declared. The layouts follow the x64 C ABI and are
/// checked against transcribe_abi_struct_size before the first model loads, so a mismatched DLL fails
/// with an error instead of reading the wrong fields.
/// </summary>
internal static class TranscribeCppNative
{
    internal const string LibraryName = "transcribe";
    internal const string LibraryFileName = "transcribe.dll";
    // From contract.json of transcribe-native-0.3.1-windows-x86_64-cpu-vulkan and include/transcribe.abihash.
    internal const string Version = "0.3.1";
    internal const string HeaderHash = "57b1af43650f195d";

    internal const int Ok = 0;
    internal const int ErrorBackend = 8;
    internal const int ErrorAborted = 13;

    internal const int BackendAuto = 0;
    internal const int BackendCpu = 1;
    internal const int BackendVulkan = 3;

    internal const uint BackendMaskCpu = 0x1;
    internal const uint BackendMaskVulkan = 0x4;

    internal const int TimestampsNone = 0;
    internal const int TimestampsToken = 4;

    internal const int DeviceTypeCpu = 0;
    internal const int DeviceTypeGpu = 1;
    internal const int DeviceTypeIntegratedGpu = 2;

    internal const int AbiModelLoadParams = 0;
    internal const int AbiSessionParams = 1;
    internal const int AbiRunParams = 2;
    internal const int AbiToken = 8;
    internal const int AbiDeviceInfo = 13;
    internal const int AbiBackendInitParams = 15;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackendInitParams
    {
        public ulong StructSize;
        public IntPtr ArtifactDirectory;
        public uint AllowedBackends;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelLoadParams
    {
        public ulong StructSize;
        public int Backend;
        public IntPtr Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionParams
    {
        public ulong StructSize;
        public int Threads;
        public int KvType;
        public int Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RunParams
    {
        public ulong StructSize;
        public int Task;
        public int Timestamps;
        public int Pnc;
        public int Itn;
        public int Diarize;
        public IntPtr Language;
        public IntPtr TargetLanguage;
        // C bool: one byte.
        public byte KeepSpecialTags;
        public IntPtr Family;
        public int SpeculativeDrafts;
        public IntPtr Vocabulary;
        public int VocabularyCount;
        public IntPtr Prompt;
        public IntPtr Prefix;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfo
    {
        public ulong StructSize;
        public IntPtr Name;
        public IntPtr Description;
        public IntPtr Kind;
        public IntPtr DeviceId;
        public ulong MemoryTotal;
        public ulong MemoryFree;
        public int DeviceType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Token
    {
        public ulong StructSize;
        public int Id;
        public float Probability;
        public long StartMilliseconds;
        public long EndMilliseconds;
        public int SegmentIndex;
        public int WordIndex;
        public IntPtr Text;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal delegate bool AbortCallback(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr message, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_version();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_status_string(int status);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint transcribe_abi_struct_size(int which);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_log_set(LogCallback? callback, IntPtr userData);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_backend_init_params_init(ref BackendInitParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_init_backends_ex(ref BackendInitParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_device_count();
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_device_get(int index);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_device_info_init(ref DeviceInfo info);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_device_get_info(IntPtr device, ref DeviceInfo info);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_model_load_params_init(ref ModelLoadParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_session_params_init(ref SessionParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_run_params_init(ref RunParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_model_load_file(byte[] utf8Path, ref ModelLoadParams parameters, out IntPtr model);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_model_free(IntPtr model);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_model_device(IntPtr model);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_session_init(IntPtr model, ref SessionParams parameters, out IntPtr session);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_session_free(IntPtr session);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_set_abort_callback(IntPtr session, AbortCallback? callback, IntPtr userData);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_run(IntPtr session, float[] pcm, int sampleCount, ref RunParams parameters);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_full_text(IntPtr session);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr transcribe_detected_language(IntPtr session);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_n_tokens(IntPtr session);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void transcribe_token_init(ref Token token);
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int transcribe_get_token(IntPtr session, int index, ref Token token);

    internal static string StatusText(int status) =>
        Marshal.PtrToStringUTF8(transcribe_status_string(status)) ?? "status " + status;

    internal static byte[] Utf8Path(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");

    /// <summary>The managed sizes the declarations above produce, keyed by transcribe_abi_struct.</summary>
    internal static IReadOnlyDictionary<int, int> ManagedStructSizes { get; } = new Dictionary<int, int>
    {
        [AbiModelLoadParams] = Marshal.SizeOf<ModelLoadParams>(),
        [AbiSessionParams] = Marshal.SizeOf<SessionParams>(),
        [AbiRunParams] = Marshal.SizeOf<RunParams>(),
        [AbiDeviceInfo] = Marshal.SizeOf<DeviceInfo>(),
        [AbiToken] = Marshal.SizeOf<Token>(),
        [AbiBackendInitParams] = Marshal.SizeOf<BackendInitParams>()
    };
}

/// <summary>One decoded token with its times in seconds from the start of the audio.</summary>
internal sealed record TranscribeCppToken(string Text, double StartSeconds, double EndSeconds);

/// <summary>Transcript of one run; tokens are empty unless they were requested.</summary>
internal sealed record TranscribeCppResult(string Text, IReadOnlyList<TranscribeCppToken> Tokens);

/// <summary>One compute device transcribe.cpp registered in this process.</summary>
internal sealed record TranscribeCppDevice(int Index, string Name, string Description, string Kind, int DeviceType, ulong MemoryTotal)
{
    internal bool IsGpu => DeviceType is TranscribeCppNative.DeviceTypeGpu or TranscribeCppNative.DeviceTypeIntegratedGpu;
    internal bool IsIntegrated => DeviceType == TranscribeCppNative.DeviceTypeIntegratedGpu;
}

/// <summary>
/// Loads transcribe.dll from an extracted native bundle and registers its backend modules. Both steps are
/// process-wide and cannot be repeated with other values: ggml keeps its device registry for the life of the
/// process, which is why a changed backend choice needs a new worker process.
/// </summary>
internal static class TranscribeCppRuntime
{
    private static readonly object Sync = new();
    private static readonly HashSet<Assembly> ResolverAssemblies = [];
    private static string? _directory;
    private static uint _mask;
    private static IntPtr _library;
    // Native code keeps the pointer for the life of the process.
    private static TranscribeCppNative.LogCallback? _logCallback;
    private static Action<int, string>? _log;
    // ggml cannot register its backends twice, so a failed start stays failed until the process ends.
    private static Exception? _startError;

    internal static bool IsInitialized { get { lock (Sync) return _library != IntPtr.Zero && _startError is null; } }
    internal static uint AllowedBackends { get { lock (Sync) return _mask; } }

    /// <summary>Checks the bundle's contract against the version these bindings were written for.</summary>
    internal static void ValidateContract(string directory)
    {
        var path = Path.Join(directory, "contract.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The transcribe.cpp runtime has no contract.json.", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var hash = root.TryGetProperty("header_hash", out var h) ? h.GetString() : null;
        if (version != TranscribeCppNative.Version || hash != TranscribeCppNative.HeaderHash)
            throw new InvalidDataException(
                $"The transcribe.cpp runtime is {version ?? "unknown"} ({hash ?? "no ABI hash"}); this plugin needs {TranscribeCppNative.Version} ({TranscribeCppNative.HeaderHash}).");
    }

    /// <summary>
    /// Loads the library and registers the allowed backends. A repeated call with the same directory and mask is a no-op;
    /// a different one throws, because the native registry is fixed after the first call.
    /// </summary>
    internal static void Initialize(string directory, uint allowedBackends, Action<int, string>? log = null)
    {
        directory = Path.GetFullPath(directory);
        lock (Sync)
        {
            if (_library != IntPtr.Zero)
            {
                if (_startError is not null)
                    throw new InvalidOperationException("transcribe.cpp failed to start in this process: " + _startError.Message, _startError);
                if (string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase) && _mask == allowedBackends) return;
                throw new InvalidOperationException("transcribe.cpp is already loaded with other backends in this process. Restart TypeWhisper to switch.");
            }
            ValidateContract(directory);
            var path = Path.Join(directory, TranscribeCppNative.LibraryFileName);
            if (!File.Exists(path)) throw new DllNotFoundException("Missing " + path);
            _directory = directory;
            RegisterResolver(typeof(TranscribeCppNative).Assembly);
            // transcribe.dll imports ggml.dll and ggml-base.dll from its own directory.
            _library = NativeLibrary.Load(path, typeof(TranscribeCppNative).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);

            try
            {
                foreach (var (which, size) in TranscribeCppNative.ManagedStructSizes)
                {
                    var native = (int)TranscribeCppNative.transcribe_abi_struct_size(which);
                    if (native != size) throw new InvalidDataException($"transcribe.cpp struct {which} has {native} bytes; the bindings expect {size}.");
                }

                if (log is not null)
                {
                    _log = log;
                    _logCallback = static (level, message, _) => _log?.Invoke(level, Marshal.PtrToStringUTF8(message)?.TrimEnd() ?? "");
                    TranscribeCppNative.transcribe_log_set(_logCallback, IntPtr.Zero);
                }

                var utf8 = Marshal.StringToCoTaskMemUTF8(directory);
                try
                {
                    var parameters = new TranscribeCppNative.BackendInitParams();
                    TranscribeCppNative.transcribe_backend_init_params_init(ref parameters);
                    parameters.ArtifactDirectory = utf8;
                    parameters.AllowedBackends = allowedBackends;
                    var status = TranscribeCppNative.transcribe_init_backends_ex(ref parameters);
                    if (status != TranscribeCppNative.Ok)
                        throw new InvalidOperationException("transcribe.cpp found no usable compute device: " + TranscribeCppNative.StatusText(status));
                }
                finally { Marshal.FreeCoTaskMem(utf8); }
                _mask = allowedBackends;
            }
            catch (Exception ex) { _startError = ex; throw; }
        }
    }

    internal static IReadOnlyList<TranscribeCppDevice> Devices()
    {
        lock (Sync) if (_library == IntPtr.Zero) return [];
        var devices = new List<TranscribeCppDevice>();
        var count = TranscribeCppNative.transcribe_device_count();
        for (var index = 0; index < count; index++)
            if (Describe(TranscribeCppNative.transcribe_device_get(index), index) is { } device) devices.Add(device);
        return devices;
    }

    internal static TranscribeCppDevice? Describe(IntPtr handle, int index)
    {
        if (handle == IntPtr.Zero) return null;
        var info = new TranscribeCppNative.DeviceInfo();
        TranscribeCppNative.transcribe_device_info_init(ref info);
        if (TranscribeCppNative.transcribe_device_get_info(handle, ref info) != TranscribeCppNative.Ok) return null;
        return new(index, Marshal.PtrToStringUTF8(info.Name) ?? "", Marshal.PtrToStringUTF8(info.Description) ?? "",
            Marshal.PtrToStringUTF8(info.Kind) ?? "", info.DeviceType, info.MemoryTotal);
    }

    /// <summary>
    /// The Vulkan device to place a model on: the first dedicated GPU, otherwise the first integrated one,
    /// the same rule as the whisper.cpp plugin. An explicit Vulkan request alone would take the first listed
    /// device, which on desktop Ryzen systems is often the processor's graphics.
    /// </summary>
    internal static TranscribeCppDevice? PreferredVulkanDevice(IEnumerable<TranscribeCppDevice> devices)
    {
        var vulkan = devices.Where(device => device.Kind == "vulkan" && device.IsGpu).ToList();
        return vulkan.FirstOrDefault(device => !device.IsIntegrated) ?? vulkan.FirstOrDefault();
    }

    internal static string? Version()
    {
        lock (Sync) if (_library == IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(TranscribeCppNative.transcribe_version());
    }

    private static void RegisterResolver(Assembly assembly)
    {
        if (!ResolverAssemblies.Add(assembly)) return;
        NativeLibrary.SetDllImportResolver(assembly, static (name, _, _) =>
            name == TranscribeCppNative.LibraryName ? _library : IntPtr.Zero);
    }
}

/// <summary>
/// One loaded model and its session. Sessions are single-threaded in transcribe.cpp; callers serialize access.
/// A run can be aborted through the session's abort callback, which the native code polls between graph steps.
/// </summary>
internal sealed class TranscribeCppSession : IDisposable
{
    private readonly TranscribeCppNative.AbortCallback _abort;
    private IntPtr _model;
    private IntPtr _session;
    private volatile bool _abortRequested;

    private TranscribeCppSession(IntPtr model, IntPtr session, TranscribeCppDevice? device)
    {
        _model = model; _session = session; Device = device;
        _abort = _ => _abortRequested;
        TranscribeCppNative.transcribe_set_abort_callback(_session, _abort, IntPtr.Zero);
    }

    /// <summary>The device that owns the model's weights.</summary>
    internal TranscribeCppDevice? Device { get; }

    internal static TranscribeCppSession Open(string modelPath, int backend, TranscribeCppDevice? device, int threads)
    {
        if (!TranscribeCppRuntime.IsInitialized) throw new InvalidOperationException("Initialize the transcribe.cpp runtime first.");
        var load = new TranscribeCppNative.ModelLoadParams();
        TranscribeCppNative.transcribe_model_load_params_init(ref load);
        load.Backend = backend;
        if (device is not null)
        {
            load.Device = TranscribeCppNative.transcribe_device_get(device.Index);
            // A null handle would silently request automatic placement.
            if (load.Device == IntPtr.Zero) throw new InvalidOperationException("The selected compute device is no longer registered.");
        }
        var status = TranscribeCppNative.transcribe_model_load_file(TranscribeCppNative.Utf8Path(modelPath), ref load, out var model);
        if (status != TranscribeCppNative.Ok) throw new InvalidOperationException("transcribe.cpp could not load the model: " + TranscribeCppNative.StatusText(status));
        try
        {
            var options = new TranscribeCppNative.SessionParams();
            TranscribeCppNative.transcribe_session_params_init(ref options);
            options.Threads = threads;
            status = TranscribeCppNative.transcribe_session_init(model, ref options, out var session);
            if (status != TranscribeCppNative.Ok) throw new InvalidOperationException("transcribe.cpp could not open a session: " + TranscribeCppNative.StatusText(status));
            return new TranscribeCppSession(model, session, TranscribeCppRuntime.Describe(TranscribeCppNative.transcribe_model_device(model), -1));
        }
        catch
        {
            TranscribeCppNative.transcribe_model_free(model);
            throw;
        }
    }

    /// <summary>Transcribes mono 16 kHz PCM. Cancellation aborts the native run and throws.</summary>
    internal string Transcribe(float[] samples, string? language, CancellationToken cancellationToken) =>
        Transcribe(samples, language, tokens: false, cancellationToken).Text;

    /// <summary>Transcribes mono 16 kHz PCM, with token times when <paramref name="tokens"/> is set.</summary>
    internal TranscribeCppResult Transcribe(float[] samples, string? language, bool tokens, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_session == IntPtr.Zero, this);
        var parameters = new TranscribeCppNative.RunParams();
        TranscribeCppNative.transcribe_run_params_init(ref parameters);
        parameters.Timestamps = tokens ? TranscribeCppNative.TimestampsToken : TranscribeCppNative.TimestampsNone;
        var languageText = string.IsNullOrWhiteSpace(language) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(language);
        parameters.Language = languageText;
        _abortRequested = false;
        try
        {
            using (cancellationToken.Register(() => _abortRequested = true))
            {
                var status = TranscribeCppNative.transcribe_run(_session, samples, samples.Length, ref parameters);
                cancellationToken.ThrowIfCancellationRequested();
                if (status != TranscribeCppNative.Ok) throw new InvalidOperationException("transcribe.cpp failed: " + TranscribeCppNative.StatusText(status));
            }
            var text = (Marshal.PtrToStringUTF8(TranscribeCppNative.transcribe_full_text(_session)) ?? "").Trim();
            return new(text, tokens ? ReadTokens() : []);
        }
        finally
        {
            if (languageText != IntPtr.Zero) Marshal.FreeCoTaskMem(languageText);
            GC.KeepAlive(_abort);
        }
    }

    // Row text is session-owned and replaced by the next run, so it is copied here.
    private TranscribeCppToken[] ReadTokens()
    {
        var count = TranscribeCppNative.transcribe_n_tokens(_session);
        var result = new TranscribeCppToken[Math.Max(0, count)];
        for (var index = 0; index < result.Length; index++)
        {
            var token = new TranscribeCppNative.Token();
            TranscribeCppNative.transcribe_token_init(ref token);
            var status = TranscribeCppNative.transcribe_get_token(_session, index, ref token);
            if (status != TranscribeCppNative.Ok) throw new InvalidOperationException("transcribe.cpp token read failed: " + TranscribeCppNative.StatusText(status));
            result[index] = new(Marshal.PtrToStringUTF8(token.Text) ?? "", token.StartMilliseconds / 1000d, token.EndMilliseconds / 1000d);
        }
        return result;
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero) { TranscribeCppNative.transcribe_session_free(_session); _session = IntPtr.Zero; }
        if (_model != IntPtr.Zero) { TranscribeCppNative.transcribe_model_free(_model); _model = IntPtr.Zero; }
    }
}

/// <summary>A Parakeet model loaded on the graphics card; the plugin serializes calls.</summary>
internal interface IGpuRecognizer : IDisposable
{
    /// <summary>Where the model runs, as shown in the processing device settings.</summary>
    TranscriptionAccelerationStatus Status { get; }
    TranscribeCppResult Transcribe(float[] samples, bool tokens, CancellationToken cancellationToken);
}

internal sealed class TranscribeCppRecognizer(TranscribeCppSession session) : IGpuRecognizer
{
    public TranscriptionAccelerationStatus Status { get; } = DescribeDevice(session.Device);

    // Parakeet v3 detects the language itself; transcribe.cpp would accept a hint but ignores it for Parakeet.
    public TranscribeCppResult Transcribe(float[] samples, bool tokens, CancellationToken cancellationToken) =>
        session.Transcribe(samples, null, tokens, cancellationToken);

    internal static TranscriptionAccelerationStatus DescribeDevice(TranscribeCppDevice? device) => device is { Kind: "vulkan" }
        ? new(TranscriptionAccelerationBackend.AmdVulkan, device.Description)
        : new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    public void Dispose() => session.Dispose();
}
