using TypeWhisper.PluginHost;
using Xunit;

public sealed class NativeModelProbeTests
{
    [Fact]
    public void OldProbeInvocationsAreRecognizedAndFailWithoutLoadingAPlugin()
    {
        string[] args =
        [
            NativeModelProbe.Argument, "--plugin-directory", Path.GetTempPath(), "--plugin-asset-directory", Path.GetTempPath(),
            "--model-id", "parakeet-tdt-0.6b", "--runtime-directory", Path.GetTempPath()
        ];
        Assert.True(NativeModelProbe.IsProbeInvocation(args));
        Assert.False(NativeModelProbe.IsProbeInvocation(["--settings", NativeModelProbe.Argument]));
        Assert.False(NativeModelProbe.IsProbeInvocation([]));
        var error = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try { Assert.Equal(NativeModelProbe.ExitLoadFailed, NativeModelProbe.Run()); }
        finally { Console.SetError(error); }
        Assert.Contains("no longer available", captured.ToString());
    }
}
