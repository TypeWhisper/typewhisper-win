using TypeWhisper.PluginHost;
using Xunit;

public sealed class NativeModelProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "probe-" + Guid.NewGuid());
    private readonly string _plugin;
    private readonly string _assets;
    private readonly string _runtime;

    public NativeModelProbeTests()
    {
        _plugin = Directory.CreateDirectory(Path.Join(_root, "plugin")).FullName;
        _assets = Directory.CreateDirectory(Path.Join(_root, "assets")).FullName;
        _runtime = Directory.CreateDirectory(Path.Join(_assets, "Runtimes", "sherpa-onnx-cuda", "v1", "native")).FullName;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // The order matches SherpaCudaRuntimeProbe in the NVIDIA Parakeet plugin.
    private string[] Arguments(string runtime, string model = "parakeet-tdt-0.6b") =>
    [
        NativeModelProbe.Argument, "--plugin-directory", _plugin, "--plugin-asset-directory", _assets,
        "--model-id", model, "--runtime-directory", runtime
    ];

    [Fact]
    public void AcceptsTheArgumentsThePluginPasses()
    {
        Assert.True(NativeModelProbe.IsProbeInvocation(Arguments(_runtime)));
        Assert.True(NativeModelProbe.TryParse(Arguments(_runtime), out var plugin, out var assets, out var model, out var runtime));
        Assert.Equal((_plugin, _assets, "parakeet-tdt-0.6b", _runtime), (plugin, assets, model, runtime));
    }

    [Fact]
    public void RejectsARuntimeOutsideThePluginCudaFolder()
    {
        var elsewhere = Directory.CreateDirectory(Path.Join(_root, "elsewhere")).FullName;
        Assert.False(NativeModelProbe.TryParse(Arguments(elsewhere), out _, out _, out _, out _));
        Assert.False(NativeModelProbe.TryParse(Arguments(Path.Join(_assets, "Runtimes", "sherpa-onnx-cuda")), out _, out _, out _, out _));
    }

    [Fact]
    public void RejectsMissingDirectoriesModelsAndUnpairedArguments()
    {
        Assert.False(NativeModelProbe.TryParse(Arguments(Path.Join(_runtime, "missing")), out _, out _, out _, out _));
        Assert.False(NativeModelProbe.TryParse(Arguments(_runtime, " "), out _, out _, out _, out _));
        Assert.False(NativeModelProbe.TryParse([.. Arguments(_runtime), "--extra"], out _, out _, out _, out _));
        Assert.False(NativeModelProbe.IsProbeInvocation(["--settings", NativeModelProbe.Argument]));
    }
}
