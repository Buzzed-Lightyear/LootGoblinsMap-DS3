using System.Runtime.InteropServices;

namespace Tracker.Memory;

/// <summary>
/// The complete native surface of the tracker.
///
/// Towards the game everything here only queries or reads: process memory through a handle
/// opened with <see cref="ReadOnlyAccess"/> (or its subset <see cref="QueryOnlyAccess"/>), and
/// the game window's position, size, minimized state and foreground state. Nothing here can
/// change another process or another program's window.
///
/// The calls that do change something act on the tracker's own windows only: extended style and
/// placement of the overlay, and global hotkeys delivered to the tracker. Those imports are
/// private and reachable only through wrappers that take an <see cref="OwnWindow"/>, which can
/// only be made from one of the tracker's own WPF windows.
/// </summary>
internal static class NativeMethods
{
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The access mask used to read the game: read and limited query, nothing more.</summary>
    public const uint ReadOnlyAccess = ProcessVmRead | ProcessQueryLimitedInformation;

    /// <summary>Subset used only to explain an "access denied" (is the game elevated?).</summary>
    public const uint QueryOnlyAccess = ProcessQueryLimitedInformation;

    public const uint TokenQuery = 0x0008;
    public const int TokenElevationClass = 20;

    public const int ErrorAccessDenied = 5;
    public const uint StillActive = 259;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    /// <summary>Opens a process for reading and limited queries only. Returns 0 on failure (see last error).</summary>
    public static nint OpenReadOnly(int processId) => OpenProcess(ReadOnlyAccess, false, processId);

    /// <summary>Opens a process for limited queries only. Returns 0 on failure.</summary>
    public static nint OpenQueryOnly(int processId) => OpenProcess(QueryOnlyAccess, false, processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, ref byte lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    /// <summary>ProcessBasicInformation (class 0) only needs the limited query right.</summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    // ---- the game window: read-only queries ---------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X, Y;
    }

    /// <summary>Walks the top-level windows: pass 0 to get the first, then the previous result to get the next.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hWnd, ref Point lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    // ---- the tracker's own windows ------------------------------------------------------

    /// <summary>A handle to one of the tracker's own windows. The only thing the changing calls below accept.</summary>
    public readonly struct OwnWindow
    {
        public readonly nint Handle;

        private OwnWindow(nint handle) => Handle = handle;

        public static OwnWindow Of(System.Windows.Window window) =>
            new(new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle());
    }

    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x00000020;   // mouse input falls through to whatever is underneath
    public const long WsExToolWindow = 0x00000080;    // no taskbar button, not in alt-tab
    public const long WsExLayered = 0x00080000;
    public const long WsExNoActivate = 0x08000000;    // never becomes the active window

    private static readonly nint HwndTopmost = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    /// <summary>Adds extended style bits to one of the tracker's own windows.</summary>
    public static void AddExtendedStyle(OwnWindow window, long bits)
    {
        long current = GetWindowLongPtrW(window.Handle, GwlExStyle);
        SetWindowLongPtrW(window.Handle, GwlExStyle, (nint)(current | bits));
    }

    public static long GetExtendedStyle(OwnWindow window) => GetWindowLongPtrW(window.Handle, GwlExStyle);

    /// <summary>Moves one of the tracker's own windows (screen pixels), keeps it topmost, never activates it.</summary>
    public static bool PlaceTopmost(OwnWindow window, int x, int y, int width, int height) =>
        SetWindowPos(window.Handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpNoOwnerZOrder);

    /// <summary>Registers a global hotkey that is delivered to one of the tracker's own windows.</summary>
    public static bool AddHotKey(OwnWindow window, int id, uint modifiers, uint virtualKey) =>
        RegisterHotKey(window.Handle, id, modifiers, virtualKey);

    public static void RemoveHotKey(OwnWindow window, int id) => UnregisterHotKey(window.Handle, id);

    // ---- controller ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    /// <summary>Reads the state of a controller. Returns 0 on success.</summary>
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, out XInputState pState);
}
