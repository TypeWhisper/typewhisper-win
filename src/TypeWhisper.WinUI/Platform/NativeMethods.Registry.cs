using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    public const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    // Lets an asynchronous registration outlive the thread that made it (Windows 8 and later).
    public const uint REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    // Returns ERROR_SUCCESS (0) or a Win32 error code. A registration reports one change and must be renewed.
    [LibraryImport("advapi32.dll")]
    public static partial int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
