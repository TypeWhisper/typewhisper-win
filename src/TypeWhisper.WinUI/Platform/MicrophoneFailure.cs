using NAudio;
using TypeWhisper.Core.Models;

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

    // Whether a device satisfies a priority entry the way AudioRecordingService resolves it (ID or name).
    internal static bool IsSameMicrophone(AudioInputDeviceInfo device, MicrophonePriorityItem item) =>
        string.Equals(device.Id, item.Id, StringComparison.OrdinalIgnoreCase) || WasapiAudioInputDeviceOrdering.DeviceNamesMatch(device.Name, item.Name);

    // The saved entry a connected device stands in for after its endpoint ID changed, for example after a driver
    // reinstall: the same name while the saved ID is gone. Unlike the resolver this ignores prefix matches, so
    // "USB Mic 2" stays a separate microphone. Returns -1 when the device is new to the list, or when several
    // disconnected entries share the name and the replaced one would be a guess.
    internal static int ReplacedEntryIndex(IReadOnlyList<MicrophonePriorityItem> priority, AudioInputDeviceInfo device, IReadOnlyList<AudioInputDeviceInfo> devices)
    {
        var name = WasapiAudioInputDeviceOrdering.NormalizeDeviceName(device.Name);
        var replaced = -1;
        for (var i = 0; i < priority.Count; i++)
        {
            var item = priority[i];
            if (!string.Equals(WasapiAudioInputDeviceOrdering.NormalizeDeviceName(item.Name), name, StringComparison.OrdinalIgnoreCase)
                || devices.Any(connected => string.Equals(connected.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (replaced >= 0)
                return -1;
            replaced = i;
        }
        return replaced;
    }

    // Resolves the priority list like AudioRecordingService does (ID, then name); null when the first entry is connected.
    internal static string? PriorityNotice(IReadOnlyList<MicrophonePriorityItem> priority, IReadOnlyList<AudioInputDeviceInfo> devices)
    {
        var missing = new List<string>();
        foreach (var item in priority)
        {
            var device = devices.FirstOrDefault(device => string.Equals(device.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(device => WasapiAudioInputDeviceOrdering.DeviceNamesMatch(device.Name, item.Name));
            if (device is not null)
                return missing.Count == 0 ? null : $"{string.Join(", ", missing)} disconnected · using {device.Name}";
            missing.Add(item.Name);
        }
        return missing.Count == 0 ? null
            : $"{string.Join(", ", missing)} disconnected. Reconnect a listed microphone or add a connected one in Audio settings.";
    }

    private const string Blocked = "Windows is blocking microphone access. Turn on Settings › Privacy & security › Microphone › Let desktop apps access your microphone.";
    private const string InUse = "Another app is using the microphone exclusively. Close that app, or turn off exclusive mode in the microphone's Windows Sound properties.";
    private const string Unavailable = "The microphone is no longer available. Reconnect it or choose another microphone in Audio settings.";
}
