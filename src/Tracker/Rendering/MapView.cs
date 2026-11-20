using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MapCore;

namespace Tracker.Rendering;

/// <summary>The map area of the window: draws the scene and handles wheel zoom and drag panning.</summary>
public sealed class MapView : FrameworkElement
{
    private readonly MapRenderer _renderer;
    private readonly TrackingSession _session;
    private Point? _dragFrom;
    private Point _dragCentre;

    public ViewState View { get; } = new();
    public string? Message { get; set; }

    /// <summary>Raised when the user changed zoom, follow or fit with the mouse, so the controls can catch up.</summary>
    public event Action? ViewChangedByUser;

    public MapView(MapRenderer renderer, TrackingSession session)
    {
        _renderer = renderer;
        _session = session;
        ClipToBounds = true;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 1 || size.Height < 1)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.Render(dc, size, _session, View, Message, overlayLines: null, pixelsPerDip: dip);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (View.FitToWindow)
        {
            // Leave fit mode at the zoom that is currently on screen, then zoom from there.
            var t = _session.Transform;
            View.Zoom = Math.Min(ActualWidth / t.Width, ActualHeight / t.Height) * 0.97;
            View.Centre = new Point(t.Width / 2.0, t.Height / 2.0);
            View.HasCentre = true;
            View.FitToWindow = false;
        }
        double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
        View.Zoom = Math.Clamp(View.Zoom * factor, ViewState.MinZoom, ViewState.MaxZoom);
        ViewChangedByUser?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (View.FitToWindow)
            return;
        _dragFrom = e.GetPosition(this);
        _dragCentre = View.Centre;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
            return;
        var now = e.GetPosition(this);
        if (View.Follow && (Math.Abs(now.X - from.X) > 3 || Math.Abs(now.Y - from.Y) > 3))
        {
            // Dragging the map means "let me look around": stop following.
            View.Follow = false;
            ViewChangedByUser?.Invoke();
        }
        if (!View.Follow)
        {
            double zoom = Math.Clamp(View.Zoom, ViewState.MinZoom, ViewState.MaxZoom);
            View.Centre = new Point(_dragCentre.X - (now.X - from.X) / zoom, _dragCentre.Y - (now.Y - from.Y) / zoom);
            View.HasCentre = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }
}
