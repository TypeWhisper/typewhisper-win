using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    // The owner keeps the delegate in a field while the subclass is installed; Windows holds only
    // its function pointer. The same delegate instance must be passed to RemoveWindowSubclass.
    public delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, IntPtr referenceData);

    // Delegates are not supported by source-generated marshalling.
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint subclassId, IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint subclassId);

    [LibraryImport("comctl32.dll")]
    public static partial IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
