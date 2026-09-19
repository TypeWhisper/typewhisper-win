using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>Runs actual Media Foundation encoding only where an AAC encoder is available.</summary>
public sealed class WindowsMediaFoundationFactAttribute : FactAttribute
{
    public WindowsMediaFoundationFactAttribute() => Skip = GetSkipReason(OperatingSystem.IsWindows(),
        () => MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, 16_000, 1).Length > 0);

    internal static string? GetSkipReason(bool windows, Func<bool> hasEncoder)
    {
        if (!windows) return "Media Foundation AAC encoding requires Windows.";
        try
        {
            return hasEncoder() ? null : "Media Foundation has no AAC encoder for 16 kHz mono audio.";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException
            || ex is TypeInitializationException { InnerException: DllNotFoundException or EntryPointNotFoundException or COMException })
        {
            return "Media Foundation AAC is unavailable; install the Windows Media Feature Pack to run this test.";
        }
    }
}
