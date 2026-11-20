using MapCore;
using Tracker.Memory;

namespace Tracker.Overlay;

/// <summary>
/// Read-only questions about the game's window: where its client area is, whether it is
/// minimized, whether it is in the foreground. Nothing here sends the window a message or
/// changes it in any way.
/// </summary>
public static class GameWindow
{
    /// <summary>The visible top-level window of the process with the largest client area, or 0.</summary>
    public static nint Find(int processId)
    {
        nint best = 0;
        long bestArea = -1;
        nint window = 0;
        // Walk the top-level windows in z-order. Bounded, in case the list changes under us.
        for (int i = 0; i < 20000; i++)
        {
            window = NativeMethods.FindWindowExW(0, window, null, null);
            if (window == 0)
                break;
            NativeMethods.GetWindowThreadProcessId(window, out int owner);
            if (owner != processId || !NativeMethods.IsWindowVisible(window))
                continue;
            long area = 0;
            if (NativeMethods.GetClientRect(window, out var rect))
                area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            // A minimized window reports an empty client area; still better than nothing.
            if (area > bestArea)
            {
                bestArea = area;
                best = window;
            }
        }
        return best;
    }

    public static GameWindowState Query(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindowVisible(window))
            return GameWindowState.Missing;

        bool minimized = NativeMethods.IsIconic(window);
        bool foreground = NativeMethods.GetForegroundWindow() == window;
        if (!NativeMethods.GetClientRect(window, out var client))
            return GameWindowState.Missing;

        var origin = new NativeMethods.Point { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(window, ref origin))
            return GameWindowState.Missing;

        var rect = new PixelRect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        return new GameWindowState(true, rect, minimized, foreground);
    }

    /// <summary>The outer rectangle of a window in screen pixels (used to read back the overlay's own placement).</summary>
    public static PixelRect OuterRect(nint window) =>
        NativeMethods.GetWindowRect(window, out var r) ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;
}
