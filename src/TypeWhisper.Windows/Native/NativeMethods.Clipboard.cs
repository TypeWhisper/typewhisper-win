using System.Runtime.InteropServices;
using System.Text;

namespace TypeWhisper.Windows.Native;

internal static partial class NativeMethods
{
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_HDROP = 15;
    public const uint CF_LOCALE = 16;
    public const uint CF_DIBV5 = 17;
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint CF_DSPTEXT = 0x0081;
    public const uint CF_DSPBITMAP = 0x0082;
    public const uint CF_DSPMETAFILEPICT = 0x0083;
    public const uint CF_DSPENHMETAFILE = 0x008E;
    public const uint CF_PRIVATEFIRST = 0x0200;
    public const uint CF_PRIVATELAST = 0x02FF;
    public const uint CF_GDIOBJFIRST = 0x0300;
    public const uint CF_GDIOBJLAST = 0x03FF;

    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint GMEM_ZEROINIT = 0x0040;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint format, IntPtr memoryHandle);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetClipboardOwner();

    [LibraryImport(
        "user32.dll",
        EntryPoint = "RegisterClipboardFormatW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial uint RegisterClipboardFormat(string formatName);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClipboardFormatNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern int GetClipboardFormatName(
        uint format,
        StringBuilder formatName,
        int maxCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr memoryHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr memoryHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr memoryHandle);

    [LibraryImport("ole32.dll")]
    public static partial IntPtr OleDuplicateData(IntPtr sourceHandle, ushort format, uint flags);

    [LibraryImport(
        "gdi32.dll",
        EntryPoint = "CopyEnhMetaFileW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    public static partial IntPtr CopyEnhMetaFile(IntPtr enhancedMetafile, string? fileName);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr graphicsObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteEnhMetaFile(IntPtr enhancedMetafile);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteMetaFile(IntPtr metafile);

    [StructLayout(LayoutKind.Sequential)]
    public struct METAFILEPICT
    {
        public int MappingMode;
        public int XExt;
        public int YExt;
        public IntPtr Metafile;
    }
}
