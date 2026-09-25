using NAudio;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Turns microphone open/start failures into actionable messages instead of one generic hint.
/// </summary>
internal static class MicrophoneFailure
{
    internal const string Generic = "Microphone could not start. Check the input device and microphone access.";

    internal static string Describe(Exception? error) => error switch
    {
        null => Generic,
        UnauthorizedAccessException => Blocked,
        MmException { Result: MmResult.AlreadyAllocated } => InUse,
        MmException { Result: MmResult.BadDeviceId or MmResult.NoDriver or MmResult.NotEnabled } => Unavailable,
        _ => (uint)error.HResult switch
        {
            0x80070005 => Blocked,
            0x8889000A or 0x8889000E => InUse,
            0x88890004 or 0x80070490 => Unavailable,
            0x88890008 => "The microphone does not support a usable recording format. In Windows Sound settings, open the microphone's properties and choose a different default format.",
            0x88890010 => "The Windows Audio service is not running. Restart it or restart Windows, then try again.",
            _ => error.InnerException is { } inner ? Describe(inner) : Generic
        }
    };

    private const string Blocked = "Windows is blocking microphone access. Turn on Settings › Privacy & security › Microphone › Let desktop apps access your microphone.";
    private const string InUse = "Another app is using the microphone exclusively. Close that app, or turn off exclusive mode in the microphone's Windows Sound properties.";
    private const string Unavailable = "The microphone is no longer available. Reconnect it or choose another microphone in Audio settings.";
}
