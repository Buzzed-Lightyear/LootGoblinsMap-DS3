namespace MapCore;

/// <summary>A rectangle in whole screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"{Width}x{Height} at ({X}, {Y})";
}

/// <summary>What the tracker learned about the game's window by asking Windows (read-only).</summary>
public readonly record struct GameWindowState(bool Found, PixelRect Client, bool Minimized, bool Foreground)
{
    public static readonly GameWindowState Missing = new(false, default, false, false);
}

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Where the overlay and its parts go. Pure arithmetic on rectangles, so it can be tested
/// without a window: the overlay covers exactly the game's client area and is only shown
/// while the game (or one of the tracker's own windows) is in the foreground.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// The overlay rectangle and whether it should be visible.
    /// <paramref name="trackerInForeground"/>: one of the tracker's own windows (settings) is the
    /// foreground window, which should not make the overlay vanish behind it.
    /// </summary>
    public static (bool Visible, PixelRect Rect) Place(GameWindowState game, bool trackerInForeground = false)
    {
        if (!game.Found || game.Minimized || game.Client.IsEmpty)
            return (false, game.Client);
        return (game.Foreground || trackerInForeground, game.Client);
    }

    /// <summary>The minimap square inside an overlay of the given size (overlay-local pixels).</summary>
    public static PixelRect Minimap(int overlayWidth, int overlayHeight, Corner corner, int size, int margin)
    {
        // Never larger than the overlay allows, never smaller than something readable.
        int room = Math.Min(overlayWidth, overlayHeight) - 2 * margin;
        size = Math.Max(Math.Min(size, room), Math.Min(64, Math.Max(room, 0)));
        int x = corner is Corner.TopLeft or Corner.BottomLeft ? margin : overlayWidth - margin - size;
        int y = corner is Corner.TopLeft or Corner.TopRight ? margin : overlayHeight - margin - size;
        return new PixelRect(x, y, size, size);
    }

    /// <summary>The large centred full-map panel: the given fraction of the overlay, centred.</summary>
    public static PixelRect FullMap(int overlayWidth, int overlayHeight, double fraction = 0.86)
    {
        fraction = Math.Clamp(fraction, 0.3, 1.0);
        int w = (int)Math.Round(overlayWidth * fraction), h = (int)Math.Round(overlayHeight * fraction);
        return new PixelRect((overlayWidth - w) / 2, (overlayHeight - h) / 2, w, h);
    }
}
