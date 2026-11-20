using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using MapCore;

namespace Tracker.Rendering;

/// <summary>How the map is placed in the window.</summary>
public sealed class ViewState
{
    public const double MinZoom = 0.1, MaxZoom = 12.0;

    /// <summary>Keep the player at the centre.</summary>
    public bool Follow = true;
    /// <summary>Show the whole map; overrides follow and zoom.</summary>
    public bool FitToWindow;
    /// <summary>Screen pixels per map pixel (the map has 6 pixels per metre by default).</summary>
    public double Zoom = 1.5;
    /// <summary>Map-pixel point shown at the centre of the view when not fitting.</summary>
    public Point Centre;
    public bool HasCentre;
}

/// <summary>
/// Draws the scene: all layers together (current one at full strength, the others dimmed),
/// bonfire and treasure markers, the trail and the player dot. Everything goes through the
/// session's one <see cref="MapTransform"/>, so the orientation toggles move map, markers,
/// dot and trail together. Used unchanged by the window and by the PNG export.
/// </summary>
public sealed class MapRenderer
{
    private static readonly Brush Background = Frozen(Color.FromRgb(0x1b, 0x1f, 0x24));
    private static readonly Color FloorColor = Color.FromRgb(0x8d, 0x98, 0xa6);
    private static readonly Brush FloorFull = Frozen(FloorColor);
    private static readonly Brush FloorDim = Frozen(Color.FromArgb(0x42, FloorColor.R, FloorColor.G, FloorColor.B));
    private static readonly Brush FloorNeutral = Frozen(Color.FromArgb(0x99, FloorColor.R, FloorColor.G, FloorColor.B));
    private static readonly Color OutlineFull = Color.FromRgb(0xe6, 0xed, 0xf3);
    private static readonly Color OutlineDim = Color.FromArgb(0x50, 0x8d, 0x98, 0xa6);
    private static readonly Brush BonfireFill = Frozen(Color.FromRgb(0xff, 0x8c, 0x1a));
    private static readonly Brush BonfireCore = Frozen(Color.FromRgb(0x4d, 0x26, 0x00));
    private static readonly Brush TreasureFill = Frozen(Color.FromRgb(0xff, 0xd6, 0x33));
    private static readonly Pen TreasurePen = FrozenPen(Color.FromRgb(0x66, 0x52, 0x00), 1.0);
    private static readonly Pen TrailPen = FrozenPen(Color.FromRgb(0x3d, 0xd6, 0xf5), 2.5);
    private static readonly Brush PlayerFill = Frozen(Color.FromRgb(0xff, 0x3b, 0x30));
    private static readonly Pen PlayerRing = FrozenPen(Colors.White, 2.0);
    private static readonly Pen PlayerLostRing = FrozenPen(Color.FromRgb(0xff, 0x3b, 0x30), 2.0);
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xe6, 0xed, 0xf3));
    private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xd0, 0x0d, 0x11, 0x17));
    private static readonly Brush BannerBrush = Frozen(Color.FromArgb(0xe6, 0x5c, 0x1f, 0x1f));
    private static readonly Typeface TextFace = new("Segoe UI");
    private static readonly Typeface MonoFace = new("Consolas");

    private readonly MapData _map;
    private readonly List<(Vector3 A, Vector3 B)>[] _outlineWorld;
    private MapTransform? _builtFor;
    private Geometry[] _fills = Array.Empty<Geometry>();
    private Geometry[] _outlines = Array.Empty<Geometry>();

    public MapRenderer(MapData map)
    {
        _map = map;
        _outlineWorld = new List<(Vector3, Vector3)>[map.LayerCount];
        for (int layer = 0; layer < map.LayerCount; layer++)
        {
            int l = layer;
            _outlineWorld[layer] = Geometry2D.OutlineEdgesXZ(map.Polygons.Where(p => p.Layer == l).Select(p => p.Vertices));
        }
    }

    /// <summary>Rebuilds the layer geometry in map-pixel space when the orientation has changed.</summary>
    private void EnsureGeometry(MapTransform t)
    {
        if (ReferenceEquals(t, _builtFor))
            return;

        var fills = new StreamGeometry[_map.LayerCount];
        var contexts = new StreamGeometryContext[_map.LayerCount];
        for (int i = 0; i < fills.Length; i++)
        {
            fills[i] = new StreamGeometry { FillRule = FillRule.Nonzero };
            contexts[i] = fills[i].Open();
        }
        foreach (var poly in _map.Polygons)
        {
            var ctx = contexts[poly.Layer];
            var (x0, y0) = t.ToPixel(poly.Vertices[0]);
            ctx.BeginFigure(new Point(x0, y0), isFilled: true, isClosed: true);
            for (int v = 1; v < poly.Vertices.Length; v++)
            {
                var (x, y) = t.ToPixel(poly.Vertices[v]);
                ctx.LineTo(new Point(x, y), isStroked: false, isSmoothJoin: false);
            }
        }
        var outlines = new StreamGeometry[_map.LayerCount];
        for (int i = 0; i < fills.Length; i++)
        {
            contexts[i].Close();
            fills[i].Freeze();

            outlines[i] = new StreamGeometry();
            using (var ctx = outlines[i].Open())
            {
                foreach (var (a, b) in _outlineWorld[i])
                {
                    var (ax, ay) = t.ToPixel(a);
                    var (bx, by) = t.ToPixel(b);
                    ctx.BeginFigure(new Point(ax, ay), isFilled: false, isClosed: false);
                    ctx.LineTo(new Point(bx, by), isStroked: true, isSmoothJoin: false);
                }
            }
            outlines[i].Freeze();
        }

        _fills = fills;
        _outlines = outlines;
        _builtFor = t;
    }

    /// <summary>Map-pixel → screen matrix for the current view. Updates the view centre while following.</summary>
    public static Matrix ComputeView(Size size, ViewState view, MapTransform t, TrackFrame frame)
    {
        var m = Matrix.Identity;
        if (view.FitToWindow)
        {
            double scale = Math.Min(size.Width / t.Width, size.Height / t.Height) * 0.97;
            if (!(scale > 0) || double.IsInfinity(scale))
                scale = 1;
            m.Translate(-t.Width / 2.0, -t.Height / 2.0);
            m.Scale(scale, scale);
            m.Translate(size.Width / 2, size.Height / 2);
            return m;
        }

        // Only follow a position that is on the map; elsewhere the view stays where it was.
        if (view.Follow && frame.HasPosition && frame.State == TrackState.Tracking)
        {
            view.Centre = new Point(frame.PixelX, frame.PixelY);
            view.HasCentre = true;
        }
        if (!view.HasCentre)
        {
            view.Centre = new Point(t.Width / 2.0, t.Height / 2.0);
            view.HasCentre = true;
        }

        double zoom = Math.Clamp(view.Zoom, ViewState.MinZoom, ViewState.MaxZoom);
        m.Translate(-view.Centre.X, -view.Centre.Y);
        m.Scale(zoom, zoom);
        m.Translate(size.Width / 2, size.Height / 2);
        return m;
    }

    /// <param name="message">Banner for the graceful states (game not running, …), or null.</param>
    /// <param name="overlayLines">Debug readout drawn onto the image (PNG export), or null.</param>
    public void Render(DrawingContext dc, Size size, TrackingSession session, ViewState view,
                       string? message, IReadOnlyList<string>? overlayLines, double pixelsPerDip = 1.0)
    {
        var t = session.Transform;
        var frame = session.Current;
        EnsureGeometry(t);

        var bounds = new Rect(size);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawRectangle(Background, null, bounds);

        Matrix m = ComputeView(size, view, t, frame);
        double scale = m.M11;
        int current = frame.State == TrackState.Tracking ? frame.Layer : session.LastLayer;

        // Map layers, in map-pixel space under the view matrix. Layers are stack order, so the
        // dimmed ones go first and the current one is painted on top.
        dc.PushTransform(new MatrixTransform(m));
        var dimPen = FrozenPen(OutlineDim, 1.0 / scale);
        for (int i = 0; i < _fills.Length; i++)
        {
            if (i == current)
                continue;
            dc.DrawGeometry(current < 0 ? FloorNeutral : FloorDim, null, _fills[i]);
            dc.DrawGeometry(null, dimPen, _outlines[i]);
        }
        if (current >= 0 && current < _fills.Length)
        {
            dc.DrawGeometry(FloorFull, null, _fills[current]);
            dc.DrawGeometry(null, FrozenPen(OutlineFull, 1.4 / scale), _outlines[current]);
        }
        dc.Pop();

        // Markers, trail and dot in screen space so their size does not change with zoom.
        var visible = Rect.Inflate(bounds, 20, 20);
        foreach (var marker in _map.Markers)
        {
            if (marker.Layer < 0)
                continue;   // not placed on any layer by the builder (same rule as its images)
            var (px, py) = t.ToPixel(marker.WorldPosition);
            var s = m.Transform(new Point(px, py));
            if (!visible.Contains(s))
                continue;

            bool onCurrent = current < 0 || marker.Layer == current;
            if (!onCurrent)
                dc.PushOpacity(0.45);
            if (marker.IsBonfire)
            {
                dc.DrawEllipse(BonfireFill, null, s, 9, 9);
                dc.DrawEllipse(BonfireCore, null, s, 3.5, 3.5);
            }
            else
            {
                var diamond = new StreamGeometry();
                using (var ctx = diamond.Open())
                {
                    ctx.BeginFigure(new Point(s.X, s.Y - 5), true, true);
                    ctx.LineTo(new Point(s.X + 5, s.Y), true, false);
                    ctx.LineTo(new Point(s.X, s.Y + 5), true, false);
                    ctx.LineTo(new Point(s.X - 5, s.Y), true, false);
                }
                dc.DrawGeometry(TreasureFill, TreasurePen, diamond);
            }
            if (!onCurrent)
                dc.Pop();
        }

        if (session.Trail.Count > 1)
        {
            var trail = new StreamGeometry();
            using (var ctx = trail.Open())
            {
                bool open = false;
                foreach (var point in session.Trail)
                {
                    if (!point.HasValue)
                    {
                        open = false;
                        continue;
                    }
                    var (px, py) = t.ToPixel(point.Value);
                    var s = m.Transform(new Point(px, py));
                    if (!open)
                    {
                        ctx.BeginFigure(s, false, false);
                        open = true;
                    }
                    else
                    {
                        ctx.LineTo(s, true, true);
                    }
                }
            }
            dc.DrawGeometry(null, TrailPen, trail);
        }

        if (frame.HasPosition)
        {
            var s = m.Transform(new Point(frame.PixelX, frame.PixelY));
            if (frame.State == TrackState.Tracking)
                dc.DrawEllipse(PlayerFill, PlayerRing, s, 5, 5);   // smaller than a bonfire marker, so one stays visible underneath
            else
                dc.DrawEllipse(null, PlayerLostRing, s, 5, 5);
        }

        if (overlayLines is { Count: > 0 })
            DrawOverlay(dc, overlayLines, pixelsPerDip);
        if (!string.IsNullOrEmpty(message))
            DrawBanner(dc, size, message, pixelsPerDip);

        dc.Pop();
    }

    private static void DrawOverlay(DrawingContext dc, IReadOnlyList<string> lines, double pixelsPerDip)
    {
        var text = new FormattedText(string.Join("\n", lines), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     MonoFace, 13, TextBrush, pixelsPerDip);
        dc.DrawRectangle(PanelBrush, null, new Rect(8, 8, text.Width + 20, text.Height + 16));
        dc.DrawText(text, new Point(18, 16));
    }

    private static void DrawBanner(DrawingContext dc, Size size, string message, double pixelsPerDip)
    {
        var text = new FormattedText(message, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                     TextFace, 16, TextBrush, pixelsPerDip)
        {
            MaxTextWidth = Math.Max(120, size.Width - 80),
            TextAlignment = TextAlignment.Center,
        };
        double w = Math.Min(size.Width - 40, text.MaxTextWidth + 30);
        double h = text.Height + 20;
        double x = (size.Width - w) / 2, y = Math.Max(12, size.Height * 0.12);
        dc.DrawRoundedRectangle(BannerBrush, null, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(text, new Point(x + 15, y + 10));
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(c), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }
}
