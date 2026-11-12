namespace TypeWhisper.PluginHost;

/// <summary>
/// Answers the native model probe that NVIDIA Parakeet versions before 1.3.0 started in a copy of the host
/// executable before they used their CUDA runtime. The probe no longer loads anything: Parakeet runs on the
/// graphics card through Vulkan now. The copy reports a failed probe at once, so such a version falls back to
/// the CPU instead of opening a second app window.
/// </summary>
public static class NativeModelProbe
{
    /// <summary>The first command-line argument that selects probe mode.</summary>
    public const string Argument = "--native-model-probe";
    internal const int ExitLoadFailed = 1;

    /// <summary>Whether the process was started as a native model probe.</summary>
    public static bool IsProbeInvocation(IReadOnlyList<string> args) => args.Count > 0 && args[0] == Argument;

    /// <summary>Returns the failure exit code without loading a plugin; the reason is written to standard error.</summary>
    public static int Run()
    {
        TranscriptionWorkerServer.SuppressCrashDialogs();
        Console.Error.WriteLine("The native model probe is no longer available. Update the NVIDIA Parakeet plugin.");
        return ExitLoadFailed;
    }
}
