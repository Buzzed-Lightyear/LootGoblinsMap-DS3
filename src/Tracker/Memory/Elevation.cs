using System.Security.Principal;

namespace Tracker.Memory;

/// <summary>Elevation checks. The tracker never runs elevated and never asks for elevation.</summary>
internal static class Elevation
{
    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Whether another process runs elevated (as administrator); null if that cannot be told.
    /// Query-only: used to explain an "access denied", since an unelevated program cannot
    /// read the memory of an elevated one.
    /// </summary>
    public static bool? IsProcessElevated(int processId)
    {
        nint process = NativeMethods.OpenQueryOnly(processId);
        if (process == 0)
            return null;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out nint token))
                return null;
            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevationClass, out int elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }
}
