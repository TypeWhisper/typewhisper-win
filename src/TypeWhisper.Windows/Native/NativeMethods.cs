using System.Runtime.InteropServices;

namespace TypeWhisper.Windows.Native;

internal static partial class NativeMethods
{
    // Hotkey modifiers
    /// <summary>
    /// Defines the mod none constant.
    /// </summary>
    public const uint MOD_NONE = 0x0000;
    /// <summary>
    /// Defines the mod alt constant.
    /// </summary>
    public const uint MOD_ALT = 0x0001;
    /// <summary>
    /// Defines the mod control constant.
    /// </summary>
    public const uint MOD_CONTROL = 0x0002;
    /// <summary>
    /// Defines the mod shift constant.
    /// </summary>
    public const uint MOD_SHIFT = 0x0004;
    /// <summary>
    /// Defines the mod win constant.
    /// </summary>
    public const uint MOD_WIN = 0x0008;
    /// <summary>
    /// Defines the mod norepeat constant.
    /// </summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // Window messages
    /// <summary>
    /// Defines the wm hotkey constant.
    /// </summary>
    public const int WM_HOTKEY = 0x0312;
    /// <summary>
    /// Defines the wm keydown constant.
    /// </summary>
    public const int WM_KEYDOWN = 0x0100;
    /// <summary>
    /// Defines the wm keyup constant.
    /// </summary>
    public const int WM_KEYUP = 0x0101;
    /// <summary>
    /// Defines the wm syskeydown constant.
    /// </summary>
    public const int WM_SYSKEYDOWN = 0x0104;
    /// <summary>
    /// Defines the wm syskeyup constant.
    /// </summary>
    public const int WM_SYSKEYUP = 0x0105;
    /// <summary>
    /// Defines the llkhf injected constant.
    /// </summary>
    public const uint LLKHF_INJECTED = 0x00000010;
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static readonly IntPtr SelfInjectedInputMarker = new(unchecked((nint)0x54574853));

    // Hook types
    /// <summary>
    /// Defines the wh keyboard ll constant.
    /// </summary>
    public const int WH_KEYBOARD_LL = 13;
    /// <summary>
    /// Defines the wh mouse ll constant.
    /// </summary>
    public const int WH_MOUSE_LL = 14;

    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;
    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;
    public const uint LLMHF_INJECTED = 0x00000001;

    // Virtual key codes
    /// <summary>
    /// Defines the vk shift constant.
    /// </summary>
    public const int VK_SHIFT = 0x10;
    /// <summary>
    /// Defines the vk control constant.
    /// </summary>
    public const int VK_CONTROL = 0x11;
    /// <summary>
    /// Defines the vk menu constant.
    /// </summary>
    public const int VK_MENU = 0x12;
    /// <summary>
    /// Defines the vk lwin constant.
    /// </summary>
    public const int VK_LWIN = 0x5B;
    /// <summary>
    /// Defines the vk rwin constant.
    /// </summary>
    public const int VK_RWIN = 0x5C;
    /// <summary>
    /// Defines the vk lshift constant.
    /// </summary>
    public const int VK_LSHIFT = 0xA0;
    /// <summary>
    /// Defines the vk rshift constant.
    /// </summary>
    public const int VK_RSHIFT = 0xA1;
    /// <summary>
    /// Defines the vk lcontrol constant.
    /// </summary>
    public const int VK_LCONTROL = 0xA2;
    /// <summary>
    /// Defines the vk rcontrol constant.
    /// </summary>
    public const int VK_RCONTROL = 0xA3;
    /// <summary>
    /// Defines the vk lmenu constant.
    /// </summary>
    public const int VK_LMENU = 0xA4;
    /// <summary>
    /// Defines the vk rmenu constant.
    /// </summary>
    public const int VK_RMENU = 0xA5;
    /// <summary>
    /// Defines the vk space constant.
    /// </summary>
    public const int VK_SPACE = 0x20;
    public const int VK_CAPITAL = 0x14;
    public const int VK_SLEEP = 0x5F;
    public const int VK_MULTIPLY = 0x6A;
    public const int VK_ADD = 0x6B;
    public const int VK_SEPARATOR = 0x6C;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_DECIMAL = 0x6E;
    public const int VK_DIVIDE = 0x6F;
    public const int VK_NUMLOCK = 0x90;
    public const int VK_APPS = 0x5D;
    public const int VK_BROWSER_BACK = 0xA6;
    public const int VK_BROWSER_FORWARD = 0xA7;
    public const int VK_BROWSER_REFRESH = 0xA8;
    public const int VK_BROWSER_STOP = 0xA9;
    public const int VK_BROWSER_SEARCH = 0xAA;
    public const int VK_BROWSER_FAVORITES = 0xAB;
    public const int VK_BROWSER_HOME = 0xAC;
    public const int VK_VOLUME_MUTE = 0xAD;
    public const int VK_VOLUME_DOWN = 0xAE;
    public const int VK_VOLUME_UP = 0xAF;
    public const int VK_MEDIA_NEXT_TRACK = 0xB0;
    public const int VK_MEDIA_PREV_TRACK = 0xB1;
    public const int VK_MEDIA_STOP = 0xB2;
    public const int VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const int VK_LAUNCH_MAIL = 0xB4;
    public const int VK_LAUNCH_MEDIA_SELECT = 0xB5;
    public const int VK_LAUNCH_APP1 = 0xB6;
    public const int VK_LAUNCH_APP2 = 0xB7;
    /// <summary>
    /// Defines the vk prior constant.
    /// </summary>
    public const int VK_PRIOR = 0x21;
    /// <summary>
    /// Defines the vk next constant.
    /// </summary>
    public const int VK_NEXT = 0x22;
    /// <summary>
    /// Defines the vk end constant.
    /// </summary>
    public const int VK_END = 0x23;
    /// <summary>
    /// Defines the vk home constant.
    /// </summary>
    public const int VK_HOME = 0x24;
    /// <summary>
    /// Defines the vk left constant.
    /// </summary>
    public const int VK_LEFT = 0x25;
    /// <summary>
    /// Defines the VK_F1 virtual-key constant.
    /// </summary>
    public const int VK_F1 = 0x70;
    /// <summary>
    /// Defines the VK_F9 virtual-key constant.
    /// </summary>
    public const int VK_F9 = 0x78;
    /// <summary>
    /// Defines the VK_F12 virtual-key constant.
    /// </summary>
    public const int VK_F12 = 0x7B;

    /// <summary>
    /// Represents the callback signature used by the low-level keyboard hook.
    /// </summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Performs register hot key.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>
    /// Performs unregister hot key.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Sets windows hook ex w.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial IntPtr SetWindowsMouseHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>
    /// Performs unhook windows hook ex.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// Performs call next hook ex.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Returns module handle w.
    /// </summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    // Language detection — reads the Windows registry directly, unaffected by parent-process culture
    /// <summary>
    /// Returns user default ui language.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    public static partial ushort GetUserDefaultUILanguage();

    /// <summary>
    /// Returns async key state.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Represents kbdllhookstruct data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        /// <summary>
        /// Gets the vk code.
        /// </summary>
        public uint vkCode;
        /// <summary>
        /// Gets the scan code.
        /// </summary>
        public uint scanCode;
        /// <summary>
        /// Gets the flags.
        /// </summary>
        public uint flags;
        /// <summary>
        /// Gets or sets the Win32 event timestamp.
        /// </summary>
        public uint time;
        /// <summary>
        /// Gets or sets extra Win32 input information.
        /// </summary>
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Active window APIs
    /// <summary>
    /// Defines the get ancestor root flag.
    /// </summary>
    public const uint GA_ROOT = 2;

    /// <summary>
    /// Returns foreground window.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    /// <summary>
    /// Returns cursor position.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Sets foreground window.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Returns an ancestor window for the supplied window handle.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>
    /// Returns window thread process id.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Returns window text w.
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    /// <summary>
    /// Returns window text length w.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetWindowTextLengthW(IntPtr hWnd);

    /// <summary>
    /// Represents a Win32 point.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>
        /// Gets or sets the x coordinate.
        /// </summary>
        public int X;

        /// <summary>
        /// Gets or sets the y coordinate.
        /// </summary>
        public int Y;
    }

    // Navigation / editing keys
    /// <summary>
    /// Defines the vk return constant.
    /// </summary>
    public const int VK_RETURN = 0x0D;
    /// <summary>
    /// Defines the vk escape constant.
    /// </summary>
    public const int VK_ESCAPE = 0x1B;
    /// <summary>
    /// Defines the VK_BACK virtual-key constant.
    /// </summary>
    public const int VK_BACK = 0x08;
    /// <summary>
    /// Defines the VK_TAB virtual-key constant.
    /// </summary>
    public const int VK_TAB = 0x09;
    /// <summary>
    /// Defines the VK_PAUSE virtual-key constant.
    /// </summary>
    public const int VK_PAUSE = 0x13;
    /// <summary>
    /// Defines the VK_UP virtual-key constant.
    /// </summary>
    public const int VK_UP = 0x26;
    /// <summary>
    /// Defines the VK_RIGHT virtual-key constant.
    /// </summary>
    public const int VK_RIGHT = 0x27;
    /// <summary>
    /// Defines the VK_DOWN virtual-key constant.
    /// </summary>
    public const int VK_DOWN = 0x28;
    /// <summary>
    /// Defines the VK_INSERT virtual-key constant.
    /// </summary>
    public const int VK_INSERT = 0x2D;
    /// <summary>
    /// Defines the VK_DELETE virtual-key constant.
    /// </summary>
    public const int VK_DELETE = 0x2E;
    /// <summary>
    /// Defines the VK_SNAPSHOT virtual-key constant.
    /// </summary>
    public const int VK_SNAPSHOT = 0x2C;
    /// <summary>
    /// Defines the VK_SCROLL virtual-key constant.
    /// </summary>
    public const int VK_SCROLL = 0x91;
    /// <summary>
    /// Defines the VK_C virtual-key constant.
    /// </summary>
    public const int VK_C = 0x43;
    /// <summary>
    /// Defines the VK_NUMPAD0 virtual-key constant.
    /// </summary>
    public const int VK_NUMPAD0 = 0x60;

    // Clipboard
    /// <summary>
    /// Defines the VK_V virtual-key constant.
    /// </summary>
    public const int VK_V = 0x56;

    // OEM keys
    /// <summary>
    /// Defines the vk oem 1 constant.
    /// </summary>
    public const int VK_OEM_1 = 0xBA;
    /// <summary>
    /// Defines the vk oem plus constant.
    /// </summary>
    public const int VK_OEM_PLUS = 0xBB;
    /// <summary>
    /// Defines the vk oem comma constant.
    /// </summary>
    public const int VK_OEM_COMMA = 0xBC;
    /// <summary>
    /// Defines the vk oem minus constant.
    /// </summary>
    public const int VK_OEM_MINUS = 0xBD;
    /// <summary>
    /// Defines the vk oem period constant.
    /// </summary>
    public const int VK_OEM_PERIOD = 0xBE;
    /// <summary>
    /// Defines the vk oem 2 constant.
    /// </summary>
    public const int VK_OEM_2 = 0xBF;
    /// <summary>
    /// Defines the vk oem 3 constant.
    /// </summary>
    public const int VK_OEM_3 = 0xC0;
    /// <summary>
    /// Defines the vk oem 4 constant.
    /// </summary>
    public const int VK_OEM_4 = 0xDB;
    /// <summary>
    /// Defines the vk oem 5 constant.
    /// </summary>
    public const int VK_OEM_5 = 0xDC;
    /// <summary>
    /// Defines the vk oem 6 constant.
    /// </summary>
    public const int VK_OEM_6 = 0xDD;
    /// <summary>
    /// Defines the vk oem 7 constant.
    /// </summary>
    public const int VK_OEM_7 = 0xDE;

    // Keyboard input simulation
    /// <summary>
    /// Sends input.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Defines the input keyboard constant.
    /// </summary>
    public const int INPUT_KEYBOARD = 1;
    /// <summary>
    /// Defines the keyeventf keyup constant.
    /// </summary>
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// Represents input data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        /// <summary>
        /// Gets the type.
        /// </summary>
        public int type;
        /// <summary>
        /// Gets the u.
        /// </summary>
        public INPUTUNION u;
    }

    /// <summary>
    /// Represents inputunion data.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    /// <summary>
    /// Represents keybdinput data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        /// <summary>
        /// Gets or sets the virtual-key code.
        /// </summary>
        public ushort wVk;
        /// <summary>
        /// Gets or sets the hardware scan code.
        /// </summary>
        public ushort wScan;
        /// <summary>
        /// Gets or sets the Win32 flags field.
        /// </summary>
        public uint dwFlags;
        /// <summary>
        /// Gets or sets the Win32 event timestamp.
        /// </summary>
        public uint time;
        /// <summary>
        /// Gets or sets extra Win32 input information.
        /// </summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Represents mouseinput data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        /// <summary>
        /// Gets the dx.
        /// </summary>
        public int dx;
        /// <summary>
        /// Gets the dy.
        /// </summary>
        public int dy;
        /// <summary>
        /// Gets the mouse data.
        /// </summary>
        public uint mouseData;
        /// <summary>
        /// Gets or sets the Win32 flags field.
        /// </summary>
        public uint dwFlags;
        /// <summary>
        /// Gets or sets the Win32 event timestamp.
        /// </summary>
        public uint time;
        /// <summary>
        /// Gets or sets extra Win32 input information.
        /// </summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Represents hardwareinput data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        /// <summary>
        /// Gets the u msg.
        /// </summary>
        public uint uMsg;
        /// <summary>
        /// Gets the w param l.
        /// </summary>
        public ushort wParamL;
        /// <summary>
        /// Gets the w param h.
        /// </summary>
        public ushort wParamH;
    }
}
