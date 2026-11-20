using System.Windows;
using System.Windows.Media;
using MapCore;
using Tracker.Memory;

namespace Tracker.Overlay;

/// <summary>
/// The overlay: a borderless, transparent, topmost window of the tracker's own that lets the
/// mouse through and never takes the focus. It is placed exactly over the game's client area.
/// It draws whatever element it is given; it knows nothing about maps.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private NativeMethods.OwnWindow _own;
    private PixelRect _placed;

    public OverlayWindow(UIElement content)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Loot Goblins Map overlay";
        Content = content;
        Left = -32000;
        Top = -32000;
        Width = 320;
        Height = 200;
    }

    public NativeMethods.OwnWindow Own => _own;

    /// <summary>The rectangle (screen pixels) the overlay was last asked to cover.</summary>
    public PixelRect Placed => _placed;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _own = NativeMethods.OwnWindow.Of(this);
        // Click-through, never active, no taskbar button.
        NativeMethods.AddExtendedStyle(_own, NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate |
                                             NativeMethods.WsExToolWindow | NativeMethods.WsExLayered);
    }

    /// <summary>The extended style bits actually on the window, for the self-check.</summary>
    public long ExtendedStyle => NativeMethods.GetExtendedStyle(_own);

    /// <summary>Covers the given screen rectangle (pixels) without activating the window.</summary>
    public void Cover(PixelRect rect)
    {
        if (rect.IsEmpty || rect == _placed)
            return;
        if (NativeMethods.PlaceTopmost(_own, rect.X, rect.Y, rect.Width, rect.Height))
            _placed = rect;
    }

    /// <summary>Where the window really is, read back from Windows.</summary>
    public PixelRect ActualRect => GameWindow.OuterRect(_own.Handle);

    public void SetShown(bool shown)
    {
        var wanted = shown ? Visibility.Visible : Visibility.Hidden;
        if (Visibility != wanted)
            Visibility = wanted;
    }
}
