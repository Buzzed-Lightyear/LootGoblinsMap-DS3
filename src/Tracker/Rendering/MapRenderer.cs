using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using MapCore;

namespace Tracker.Rendering;

/// <summary>How the map is placed in its panel.</summary>
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

/// <summary>What a panel looks like and what it shows besides the map itself.</summary>
public sealed class RenderStyle
{
    /// <summary>Solid background (debug window, PNG export). False: a translucent rounded panel for the overlay.</summary>
    public bool Opaque = true;
    /// <summary>Opacity of a translucent panel as a whole.</summary>
    public double PanelOpacity = 0.85;
    /// <summary>Highlight this layer instead of the one the player is on (full-map layer stepping).</summary>
    public int? ShownLayer;
    /// <summary>How many of the markers nearest to the player get a name label; -1 labels all that fit.</summary>
    public int LabelCount;
    public bool DrawTrail = true;
    public string? Title;
    public string? Footer;
    /// <summary>List the categories on the panel with their colours and how many are left.</summary>
    public bool Legend;
    public TrackerSettings Settings = new();
}

/// <summary>
/// Draws a map panel: all layers together (shown one at full strength, the others dimmed),
/// markers coloured by category with collected ones hidden or dimmed, the trail and the player
/// dot, and optionally labels, a title, a legend and a message. Everything goes through the
/// session's one <see cref="MapTransform"/>, so orientation changes move map, markers, dot and
/// trail together. Used unchanged by the debug window, the overlay and the PNG export.
/// </summary>
public sealed class MapRenderer
{
    private static readonly Brush Background = Frozen(Color.FromRgb(0x1b, 0x1f, 0x24));
    private static readonly Brush PanelBackground = Frozen(Color.FromArgb(0xe0, 0x10, 0x14, 0x1a));
    private static readonly Pen PanelBorder = FrozenPen(Color.FromArgb(0xa0, 0xe6, 0xed, 0xf3), 1.2);
    private static readonly Color FloorColor = Color.FromRgb(0x8d, 0x98, 0xa6);
    private static readonly Brush FloorFull = Frozen(FloorColor);
    private static readonly Brush FloorDim = Frozen(Color.FromArgb(0x42, FloorColor.R, FloorColor.G, FloorColor.B));
    private static readonly Brush FloorNeutral = Frozen(Color.FromArgb(0x99, FloorColor.R, FloorColor.G, FloorColor.B));
    private static readonly Color OutlineFull = Color.FromRgb(0xe6, 0xed, 0xf3);
    private static readonly Color OutlineDim = Color.FromArgb(0x50, 0x8d, 0x98, 0xa6);
    private static readonly Brush BonfireFill = Frozen(Color.FromRgb(0xff, 0x8c, 0x1a));
    private static readonly Brush BonfireCore = Frozen(Color.FromRgb(0x4d, 0x26, 0x00));
    private static readonly Pen MarkerPen = FrozenPen(Color.FromArgb(0xd0, 0x10, 0x14, 0x1a), 1.2);
    private static readonly Pen TrailPen = FrozenPen(Color.FromRgb(0x3d, 0xd6, 0xf5), 2.5);
    private static readonly Brush PlayerFill = Frozen(Color.FromRgb(0xff, 0x3b, 0x30));
    private static readonly Pen PlayerRing = FrozenPen(Colors.White, 2.0);
    private static readonly Pen PlayerLostRing = FrozenPen(Color.FromRgb(0xff, 0x3b, 0x30), 2.0);
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xf2, 0xf5, 0xf8));
    private static readonly Brush MutedText = Frozen(Color.FromRgb(0xb6, 0xc0, 0xcc));
    private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xd8, 0x0d, 0x11, 0x17));
    private static readonly Brush BannerBrush = Frozen(Color.FromArgb(0xe6, 0x5c, 0x1f, 0x1f));
    private static readonly Typeface TextFace = new("Segoe UI");
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoFace = new("Consolas");

    /// <summary>Marker colour per category. Chosen to stay apart from each other, the orange bonfires, the red player dot and the cyan trail.</summary>
    public static readonly IReadOnlyDictionary<string, Color> CategoryColors = new Dictionary<string, Color>
    {
        [ItemCategories.Key] = Color.FromRgb(0xff, 0xd6, 0x0a),
        [ItemCategories.Ring] = Color.FromRgb(0xc7, 0x7d, 0xff),
        [ItemCategories.Weapon] = Color.FromRgb(0xff, 0x70, 0xa6),
        [ItemCategories.Armor] = Color.FromRgb(0x06, 0xd6, 0xa0),
        [ItemCategories.Spell] = Color.FromRgb(0x5a, 0xa9, 0xff),
        [ItemCategories.Upgrade] = Color.FromRgb(0xb5, 0xe4, 0x8c),
        [ItemCategories.Soul] = Color.FromRgb(0xf8, 0xf9, 0xfa),
        [ItemCategories.Consumable] = Color.FromRgb(0xad, 0xb5, 0xbd),
        [ItemCategories.Unknown] = Color.FromRgb(0x8a, 0x6f, 0x5a),
    };

    private static readonly Dictionary<string, Brush> CategoryBrushes = CategoryColors.ToDictionary(kv => kv.Key, kv => Frozen(kv.Value));

    private readonly Dictionary<MapData, List<(Vector3 A, Vector3 B)>[]> _outlines = new();
    private MapTransform? _builtFor;

    /// <summary>
    /// A square piece of one layer in map-pixel space. The map is cut into tiles so that a view
    /// showing a small part of it (the minimap) only has to draw the tiles it can see.
    /// </summary>
    private sealed class Tile
    {
        public Rect Bounds = Rect.Empty;
        public StreamGeometry Fill = new() { FillRule = FillRule.Nonzero };
        public StreamGeometry Edges = new();
        public StreamGeometryContext? FillContext, EdgeContext;
    }

    /// <summary>Tile edge in map pixels: 32 m at the default 6 pixels per metre.</summary>
    private const double TileSize = 192;

    private Tile[][] _tiles = Array.Empty<Tile[]>();

    private List<(Vector3 A, Vector3 B)>[] OutlinesOf(MapData map)
    {
        if (_outlines.TryGetValue(map, out var cached))
            return cached;
        var outlines = new List<(Vector3, Vector3)>[map.LayerCount];
        for (int layer = 0; layer < map.LayerCount; layer++)
        {
            int l = layer;
            outlines[layer] = Geometry2D.OutlineEdgesXZ(map.Polygons.Where(p => p.Layer == l).Select(p => p.Vertices));
        }
        return _outlines[map] = outlines;
    }

    /// <summary>Rebuilds the layer geometry in map-pixel space when the map or the orientation has changed.</summary>
    private void EnsureGeometry(MapData map, MapTransform t)
    {
        if (ReferenceEquals(t, _builtFor))
            return;

        var tiles = new Dictionary<(int, int), Tile>[map.LayerCount];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = new Dictionary<(int, int), Tile>();

        Tile TileAt(int layer, double x, double y)
        {
            var key = ((int)Math.Floor(x / TileSize), (int)Math.Floor(y / TileSize));
            if (!tiles[layer].TryGetValue(key, out var tile))
            {
                tiles[layer][key] = tile = new Tile();
                tile.FillContext = tile.Fill.Open();
                tile.EdgeContext = tile.Edges.Open();
            }
            return tile;
        }

        var points = new List<Point>(8);
        foreach (var poly in map.Polygons)
        {
            points.Clear();
            double cx = 0, cy = 0;
            foreach (var v in poly.Vertices)
            {
                var (x, y) = t.ToPixel(v);
                points.Add(new Point(x, y));
                cx += x;
                cy += y;
            }
            // A polygon goes into the tile its centre is in; the tile's bounds grow to hold all of it.
            var tile = TileAt(poly.Layer, cx / points.Count, cy / points.Count);
            tile.FillContext!.BeginFigure(points[0], isFilled: true, isClosed: true);
            for (int v = 1; v < points.Count; v++)
                tile.FillContext.LineTo(points[v], isStroked: false, isSmoothJoin: false);
            foreach (var p in points)
                tile.Bounds.Union(p);
        }

        var outlineWorld = OutlinesOf(map);
        for (int layer = 0; layer < map.LayerCount; layer++)
        {
            foreach (var (a, b) in outlineWorld[layer])
            {
                var (ax, ay) = t.ToPixel(a);
                var (bx, by) = t.ToPixel(b);
                var tile = TileAt(layer, (ax + bx) / 2, (ay + by) / 2);
                tile.EdgeContext!.BeginFigure(new Point(ax, ay), isFilled: false, isClosed: false);
                tile.EdgeContext.LineTo(new Point(bx, by), isStroked: true, isSmoothJoin: false);
                tile.Bounds.Union(new Point(ax, ay));
                tile.Bounds.Union(new Point(bx, by));
            }
        }

        _tiles = new Tile[map.LayerCount][];
        for (int layer = 0; layer < map.LayerCount; layer++)
        {
            foreach (var tile in tiles[layer].Values)
            {
                tile.FillContext!.Close();
                tile.EdgeContext!.Close();
                tile.Fill.Freeze();
                tile.Edges.Freeze();
            }
            _tiles[layer] = tiles[layer].Values.ToArray();
        }
        _builtFor = t;
    }

    /// <summary>Map-pixel → panel matrix for the current view. Updates the view centre while following.</summary>
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

    /// <summary>Draws one panel into <paramref name="viewport"/>.</summary>
    /// <param name="message">Banner for the graceful states (game not running, …), or null.</param>
    /// <param name="overlayLines">Debug readout drawn onto the panel (PNG export), or null.</param>
    public void Render(DrawingContext dc, Rect viewport, TrackingSession session, ViewState view, RenderStyle style,
                       string? message = null, IReadOnlyList<string>? overlayLines = null, double pixelsPerDip = 1.0)
    {
        var map = session.Map;
        var t = session.Transform;
        var frame = session.Current;
        var settings = style.Settings;
        EnsureGeometry(map, t);

        var size = viewport.Size;
        var bounds = new Rect(size);
        double corner = style.Opaque ? 0 : 10;
        dc.PushTransform(new TranslateTransform(viewport.X, viewport.Y));
        if (!style.Opaque)
            dc.PushOpacity(Math.Clamp(style.PanelOpacity, 0.2, 1.0));
        dc.PushClip(new RectangleGeometry(bounds, corner, corner));
        dc.DrawRectangle(style.Opaque ? Background : PanelBackground, null, bounds);

        Matrix m = ComputeView(size, view, t, frame);
        double scale = m.M11;
        int playerLayer = frame.State == TrackState.Tracking ? frame.Layer : session.LastLayer;
        int current = style.ShownLayer is { } forced && forced >= 0 && forced < _tiles.Length ? forced : playerLayer;

        // Map layers, in map-pixel space under the view matrix. The dimmed ones go first and the
        // shown one is painted on top.
        // Only the tiles the panel can see are drawn.
        var toMap = m;
        toMap.Invert();
        var seen = Rect.Transform(bounds, toMap);
        seen.Inflate(4, 4);

        dc.PushTransform(new MatrixTransform(m));
        var dimPen = FrozenPen(OutlineDim, 1.0 / scale);
        var dimFill = current < 0 ? FloorNeutral : FloorDim;
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (i == current)
                continue;
            foreach (var tile in _tiles[i])
            {
                if (!tile.Bounds.IntersectsWith(seen))
                    continue;
                dc.DrawGeometry(dimFill, null, tile.Fill);
                dc.DrawGeometry(null, dimPen, tile.Edges);
            }
        }
        if (current >= 0 && current < _tiles.Length)
        {
            var fullPen = FrozenPen(OutlineFull, 1.4 / scale);
            foreach (var tile in _tiles[current])
            {
                if (!tile.Bounds.IntersectsWith(seen))
                    continue;
                dc.DrawGeometry(FloorFull, null, tile.Fill);
                dc.DrawGeometry(null, fullPen, tile.Edges);
            }
        }
        dc.Pop();

        // Trail under the markers.
        if (style.DrawTrail && session.Trail.Count > 1)
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

        // Markers in panel space so their size does not change with zoom.
        var visible = Rect.Inflate(bounds, 12, 12);
        var drawn = new List<(MapDocument.MarkerInfo Marker, Point At, float Distance)>();
        foreach (var marker in map.Markers)
        {
            var visibility = session.VisibilityOf(marker, settings);
            if (visibility == MarkerVisibility.Hidden)
                continue;
            var (px, py) = t.ToPixel(marker.WorldPosition);
            var s = m.Transform(new Point(px, py));
            if (!visible.Contains(s))
                continue;

            double opacity = 1.0;
            if (current >= 0 && marker.Layer != current)
                opacity = 0.45;
            if (visibility == MarkerVisibility.Dimmed)
                opacity *= 0.35;
            if (opacity < 1.0)
                dc.PushOpacity(opacity);
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
                    ctx.BeginFigure(new Point(s.X, s.Y - 6), true, true);
                    ctx.LineTo(new Point(s.X + 6, s.Y), true, false);
                    ctx.LineTo(new Point(s.X, s.Y + 6), true, false);
                    ctx.LineTo(new Point(s.X - 6, s.Y), true, false);
                }
                dc.DrawGeometry(BrushFor(marker.Category), MarkerPen, diamond);
            }
            if (opacity < 1.0)
                dc.Pop();

            if (visibility == MarkerVisibility.Shown && bounds.Contains(s))
            {
                float distance = frame.HasPosition ? Vector3.Distance(marker.WorldPosition, frame.World) : 0f;
                drawn.Add((marker, s, distance));
            }
        }

        if (frame.HasPosition)
        {
            var s = m.Transform(new Point(frame.PixelX, frame.PixelY));
            if (frame.State == TrackState.Tracking)
                dc.DrawEllipse(PlayerFill, PlayerRing, s, 5, 5);   // smaller than a bonfire marker, so one stays visible underneath
            else
                dc.DrawEllipse(null, PlayerLostRing, s, 5, 5);
        }

        if (style.LabelCount != 0)
            DrawLabels(dc, bounds, drawn, style.LabelCount, pixelsPerDip);
        if (style.Legend)
            DrawLegend(dc, bounds, session, settings, pixelsPerDip);
        if (!string.IsNullOrEmpty(style.Title))
            DrawCaption(dc, style.Title, new Point(14, 10), 17, BoldFace, pixelsPerDip);
        if (!string.IsNullOrEmpty(style.Footer))
        {
            var footer = Text(style.Footer, 13, MutedText, TextFace, pixelsPerDip);
            dc.DrawRectangle(LabelBack, null, new Rect(8, size.Height - footer.Height - 16, footer.Width + 14, footer.Height + 8));
            dc.DrawText(footer, new Point(15, size.Height - footer.Height - 12));
        }

        if (overlayLines is { Count: > 0 })
        {
            var text = Text(string.Join("\n", overlayLines), 13, TextBrush, MonoFace, pixelsPerDip);
            dc.DrawRectangle(LabelBack, null, new Rect(8, 8, text.Width + 20, text.Height + 16));
            dc.DrawText(text, new Point(18, 16));
        }
        if (!string.IsNullOrEmpty(message))
            DrawBanner(dc, size, message, pixelsPerDip);

        if (!style.Opaque)
            dc.DrawRoundedRectangle(null, PanelBorder, new Rect(0.6, 0.6, size.Width - 1.2, size.Height - 1.2), corner, corner);
        dc.Pop();   // clip
        if (!style.Opaque)
            dc.Pop();   // opacity
        dc.Pop();   // translate
    }

    private static Brush BrushFor(string? category) =>
        CategoryBrushes.TryGetValue(category ?? ItemCategories.Unknown, out var brush) ? brush : CategoryBrushes[ItemCategories.Unknown];

    /// <summary>Names the nearest markers. A label that would cover another label is left out, so what is shown stays readable.</summary>
    private static void DrawLabels(DrawingContext dc, Rect bounds, List<(MapDocument.MarkerInfo Marker, Point At, float Distance)> markers,
                                   int count, double pixelsPerDip)
    {
        var placed = new List<Rect>();
        int shown = 0;
        foreach (var (marker, at, _) in markers.OrderBy(x => x.Distance))
        {
            if (count >= 0 && shown >= count)
                break;
            string label = marker.Label;
            if (label.Length > 46)
                label = label[..45] + "…";
            var text = Text(label, 12.5, TextBrush, TextFace, pixelsPerDip);
            double w = text.Width + 10, h = text.Height + 4;

            // To the right of the marker; to the left when that would leave the panel.
            var box = new Rect(at.X + 10, at.Y - h / 2, w, h);
            if (box.Right > bounds.Right - 4)
                box = new Rect(at.X - 10 - w, at.Y - h / 2, w, h);
            if (box.Left < bounds.Left + 4 || box.Top < bounds.Top + 4 || box.Bottom > bounds.Bottom - 4)
                continue;
            var padded = Rect.Inflate(box, 2, 2);
            if (placed.Any(p => p.IntersectsWith(padded)))
                continue;

            placed.Add(box);
            dc.DrawRoundedRectangle(LabelBack, null, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 5, box.Y + 2));
            shown++;
        }
    }

    private static void DrawLegend(DrawingContext dc, Rect bounds, TrackingSession session, TrackerSettings settings, double pixelsPerDip)
    {
        var treasures = session.Map.Markers.Where(mk => !mk.IsBonfire && !mk.Unused && mk.Layer >= 0).ToList();
        var rows = new List<(Brush Swatch, bool Round, string Line)>();
        foreach (string category in ItemCategories.All)
        {
            var inCategory = treasures.Where(mk => (mk.Category ?? ItemCategories.Unknown) == category).ToList();
            if (inCategory.Count == 0)
                continue;
            int left = inCategory.Count(mk => !session.IsCollected(mk));
            string hidden = settings.IsCategoryShown(category) ? "" : " (hidden)";
            rows.Add((BrushFor(category), false, $"{ItemCategories.DisplayNames[category]}  {left} of {inCategory.Count} left{hidden}"));
        }
        rows.Add((BonfireFill, true, "Bonfire"));
        rows.Add((PlayerFill, true, "You"));

        var texts = rows.Select(r => Text(r.Line, 13, TextBrush, TextFace, pixelsPerDip)).ToList();
        double width = texts.Max(x => x.Width) + 44, lineHeight = texts[0].Height + 5;
        double height = rows.Count * lineHeight + 14;
        var box = new Rect(bounds.Right - width - 12, bounds.Bottom - height - 12, width, height);
        dc.DrawRoundedRectangle(LabelBack, null, box, 5, 5);
        for (int i = 0; i < rows.Count; i++)
        {
            double y = box.Y + 8 + i * lineHeight;
            var centre = new Point(box.X + 18, y + texts[i].Height / 2);
            if (rows[i].Round)
            {
                dc.DrawEllipse(rows[i].Swatch, null, centre, 6, 6);
            }
            else
            {
                var diamond = new StreamGeometry();
                using (var ctx = diamond.Open())
                {
                    ctx.BeginFigure(new Point(centre.X, centre.Y - 6), true, true);
                    ctx.LineTo(new Point(centre.X + 6, centre.Y), true, false);
                    ctx.LineTo(new Point(centre.X, centre.Y + 6), true, false);
                    ctx.LineTo(new Point(centre.X - 6, centre.Y), true, false);
                }
                dc.DrawGeometry(rows[i].Swatch, MarkerPen, diamond);
            }
            dc.DrawText(texts[i], new Point(box.X + 34, y));
        }
    }

    private static void DrawCaption(DrawingContext dc, string caption, Point at, double fontSize, Typeface face, double pixelsPerDip)
    {
        var text = Text(caption, fontSize, TextBrush, face, pixelsPerDip);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X - 6, at.Y - 3, text.Width + 12, text.Height + 6), 4, 4);
        dc.DrawText(text, at);
    }

    private static void DrawBanner(DrawingContext dc, Size size, string message, double pixelsPerDip)
    {
        var text = Text(message, Math.Min(16, Math.Max(11, size.Width / 26)), TextBrush, TextFace, pixelsPerDip);
        text.MaxTextWidth = Math.Max(80, size.Width - 40);
        text.TextAlignment = TextAlignment.Center;
        double w = text.MaxTextWidth + 20, h = text.Height + 16;
        double x = (size.Width - w) / 2, y = Math.Max(8, size.Height * 0.12);
        dc.DrawRoundedRectangle(BannerBrush, null, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(text, new Point(x + 10, y + 8));
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface face, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);

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
