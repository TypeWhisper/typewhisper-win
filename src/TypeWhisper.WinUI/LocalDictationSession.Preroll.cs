using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    private readonly MicrophonePrerollSuspension _prerollSuspension = new();

    internal void ObservePrerollSession(uint message, long reason, bool notificationsAvailable)
    {
        var suspended = _prerollSuspension.Observe(message, reason, PrerollDesktop.IsInteractive());
        _audio.SuspendMicrophonePreroll(!notificationsAvailable || suspended || Platform.RemoteSession.IsActive());
    }
}

internal static class PrerollDesktop
{
    internal static bool IsInteractive()
    {
        // A process started while locked must not arm before its first session notification.
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)
                && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
    private static extern bool GetUserObjectInformation(IntPtr desktop, int index, StringBuilder value, int bytes, out int needed);
}
