using System.Runtime.InteropServices;

namespace Tracker.Memory;

/// <summary>
/// The complete native surface of the tracker. Everything here either queries or reads;
/// there is no call that changes another process. The open call is private and reachable
/// only through the two wrappers below, so the access masks ever requested are
/// <see cref="ReadOnlyAccess"/> (read + limited query) and its subset <see cref="QueryOnlyAccess"/>.
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
}
