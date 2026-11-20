using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

/// <summary>What a panel is and what it shows besides the map itself. How it looks comes from the <see cref="Theme"/>.</summary>
public sealed class RenderStyle
{
    /// <summary>Solid background (debug window, PNG export). False: a translucent panel for the overlay.</summary>
    public bool Opaque = true;
    /// <summary>Opacity of a translucent panel as a whole.</summary>
    public double PanelOpacity = 0.85;
    /// <summary>The minimap: may be drawn as a disc, and gets no paper texture.</summary>
    public bool Minimap;
    /// <summary>Paper grain and vignette, where the theme has them (full map, exports).</summary>
    public bool Paper;
    /// <summary>Highlight this layer instead of the one the player is on (full-map layer stepping).</summary>
    public int? ShownLayer;
    /// <summary>How many of the markers nearest to the player get a name label; -1 labels all that fit.</summary>
    public int LabelCount;
    public bool DrawTrail = true;
    public string? Title;
    public string? Footer;
    /// <summary>A large caption shown briefly, for example the area's name after a map switch.</summary>
    public string? Banner;
    /// <summary>List the categories on the panel with their icons and how many are left.</summary>
    public bool Legend;
    public TrackerSettings Settings = new();
}

/// <summary>
/// Draws a map panel in the current <see cref="Theme"/>: all layers together (the shown one at
/// full strength, lower ones faded back, upper ones casting a faint shadow), hatching on stairs
/// and ramps, markers as category icons with collected ones hidden or dimmed, the trail and the
/// player, and optionally labels, a title, a legend and messages. Everything goes through the
/// session's one <see cref="MapTransform"/>, so orientation changes move map, markers, player and
/// trail together. Used unchanged by the debug window, the overlay and the PNG export.
/// </summary>
public sealed class MapRenderer
{
    // ---- theme resources -----------------------------------------------------------------

    private Theme _theme = Theme.Plain();
    private Look _look = new(Theme.Plain());

    public Theme Theme
    {
        get => _theme;
        set
        {
            _theme = value ?? Theme.Plain();
            _look = new Look(_theme);
            _builtFor = null;   // hatch spacing and steepness belong to the theme
        }
    }

    /// <summary>Folder with the bundled font, next to the program.</summary>
    public static string FontsFolder => Path.Combine(AppContext.BaseDirectory, "Fonts");
    public const string BundledFontFamily = "EB Garamond";

    /// <summary>Brushes, pens and typefaces made once per theme.</summary>
    private sealed class Look
    {
        public readonly Brush Background, Panel, Floor, FloorOther, FloorAbove, FloorNeutral, Shadow, Bonfire, BonfireCore, Player, Text, TextMuted, LabelBack, Banner;
        public readonly Brush? Paper, IconPlate;
        public readonly Color ShadowColour;
        public readonly Color Outline, OutlineOther, Hatch, MarkerOutline, PanelBorder;
        public readonly Pen PlayerRing, PlayerLost, Trail, MarkerPen;
        public readonly Typeface LabelFace, TitleFace, MonoFace = new("Consolas");
        public readonly Dictionary<string, Brush> Category = new();
        public readonly bool HasHatch;

        public Look(Theme t)
        {
            Background = Solid(t.Background); Panel = Solid(t.Panel);
            Floor = Solid(t.Floor); FloorOther = Solid(t.FloorOther); FloorNeutral = Solid(t.FloorNeutral);
            FloorAbove = string.IsNullOrWhiteSpace(t.FloorAbove) ? FloorOther : Solid(t.FloorAbove);
            Paper = string.IsNullOrWhiteSpace(t.Paper) ? null : Solid(t.Paper);
            IconPlate = string.IsNullOrWhiteSpace(t.IconPlate) ? null : Solid(t.IconPlate);
            Shadow = Solid(t.Shadow);
            ShadowColour = Theme.ParseColor(t.Shadow);
            Bonfire = Solid(t.Bonfire); BonfireCore = Solid(t.BonfireCore);
            Player = Solid(t.Player); Text = Solid(t.Text); TextMuted = Solid(t.TextMuted);
            LabelBack = Solid(t.LabelBackground); Banner = Solid(t.Banner);
            Outline = Theme.ParseColor(t.Outline); OutlineOther = Theme.ParseColor(t.OutlineOther);
            HasHatch = !string.IsNullOrWhiteSpace(t.Hatch);
            Hatch = HasHatch ? Theme.ParseColor(t.Hatch) : Colors.Transparent;
            MarkerOutline = Theme.ParseColor(t.MarkerOutline);
            PanelBorder = Theme.ParseColor(t.PanelBorder);
            PlayerRing = MakePen(Theme.ParseColor(t.PlayerRing), 2.0);
            PlayerLost = MakePen(Theme.ParseColor(t.Player), 2.0);
            Trail = MakePen(Theme.ParseColor(t.Trail), t.TrailWidth);
            MarkerPen = MakePen(MarkerOutline, t.MarkerOutlineWidth);
            foreach (var (category, colour) in t.CategoryColors)
                Category[category] = Solid(colour);
            LabelFace = Face(t.LabelFont, FontWeights.Medium);
            TitleFace = Face(t.TitleFont, FontWeights.SemiBold);
        }

        /// <summary>"bundled" is the serif shipped with the app; when its file is missing a system serif stands in.</summary>
        private static Typeface Face(string? name, FontWeight weight)
        {
            if (string.Equals(name, "bundled", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(FontsFolder) && Directory.EnumerateFiles(FontsFolder, "*.ttf").Any())
                    {
                        var family = new FontFamily(new Uri(FontsFolder + Path.DirectorySeparatorChar), "./#" + BundledFontFamily);
                        return new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal, new FontFamily("Georgia"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UriFormatException or UnauthorizedAccessException) { }
                return new Typeface(new FontFamily("Georgia"), FontStyles.Normal, weight, FontStretches.Normal);
            }
            return new Typeface(new FontFamily(string.IsNullOrWhiteSpace(name) ? "Segoe UI" : name), FontStyles.Normal,
                                weight == FontWeights.Medium ? FontWeights.Normal : weight, FontStretches.Normal);
        }
    }

    private static Brush Solid(string? colour)
    {
        var b = new SolidColorBrush(Theme.ParseColor(colour));
        b.Freeze();
        return b;
    }

    private static Pen MakePen(Color c, double thickness)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        var p = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    // ---- icons ---------------------------------------------------------------------------

    /// <summary>
    /// Original vector icons, one per category, drawn in a box from -1 to 1 and scaled to the
    /// theme's icon size. Simple filled silhouettes so they read at minimap size.
    /// </summary>
    private static readonly Dictionary<string, Geometry> Icons = new()
    {
        [ItemCategories.Key] = Icon("M -0.95,0 A 0.45,0.45 0 1 1 -0.05,0 A 0.45,0.45 0 1 1 -0.95,0 Z M -0.68,0 A 0.18,0.18 0 1 0 -0.32,0 A 0.18,0.18 0 1 0 -0.68,0 Z " +
                                    "M -0.08,-0.12 L 0.95,-0.12 L 0.95,0.42 L 0.78,0.42 L 0.78,0.12 L 0.62,0.12 L 0.62,0.36 L 0.45,0.36 L 0.45,0.12 L -0.08,0.12 Z", evenOdd: true),
        [ItemCategories.Ring] = Icon("M -0.8,0.15 A 0.8,0.8 0 1 1 0.8,0.15 A 0.8,0.8 0 1 1 -0.8,0.15 Z M -0.5,0.15 A 0.5,0.5 0 1 0 0.5,0.15 A 0.5,0.5 0 1 0 -0.5,0.15 Z " +
                                     "M 0,-1 L 0.28,-0.7 L 0,-0.42 L -0.28,-0.7 Z", evenOdd: true),
        [ItemCategories.Weapon] = Icon("M 0,-1 L 0.22,-0.74 L 0.22,0.22 L 0.66,0.22 L 0.66,0.48 L 0.16,0.48 L 0.16,0.8 L 0.24,0.9 L 0,1 L -0.24,0.9 L -0.16,0.8 L -0.16,0.48 L -0.66,0.48 L -0.66,0.22 L -0.22,0.22 L -0.22,-0.74 Z"),
        [ItemCategories.Shield] = Icon("M -0.78,-0.85 L 0.78,-0.85 L 0.78,0 C 0.78,0.6 0.3,0.9 0,1 C -0.3,0.9 -0.78,0.6 -0.78,0 Z"),
        [ItemCategories.Catalyst] = Icon("M -0.85,0.82 L -0.67,1 L 0.32,-0.1 L 0.14,-0.28 Z M 0.1,-0.58 A 0.42,0.42 0 1 1 0.94,-0.58 A 0.42,0.42 0 1 1 0.1,-0.58 Z"),
        [ItemCategories.Armor] = Icon("M -0.85,-0.7 L -0.32,-0.92 L 0,-0.6 L 0.32,-0.92 L 0.85,-0.7 L 0.62,0.05 L 0.72,0.95 L -0.72,0.95 L -0.62,0.05 Z"),
        [ItemCategories.Spell] = Icon("M 0,-1 L 0.24,-0.24 L 1,0 L 0.24,0.24 L 0,1 L -0.24,0.24 L -1,0 L -0.24,-0.24 Z"),
        [ItemCategories.Upgrade] = Icon("M 0,-1 L 0.62,-0.42 L 0.46,0.68 L 0,1 L -0.46,0.68 L -0.62,-0.42 Z"),
        [ItemCategories.Soul] = Icon("M 0,-1 C 0.7,-0.2 0.78,0.3 0.52,0.66 C 0.3,0.96 -0.3,0.96 -0.52,0.66 C -0.78,0.3 -0.7,-0.2 0,-1 Z"),
        [ItemCategories.Ammo] = Icon("M 0,-1 L 0.36,-0.42 L 0.08,-0.42 L 0.08,0.4 L 0.42,0.72 L 0.42,1 L 0,0.68 L -0.42,1 L -0.42,0.72 L -0.08,0.4 L -0.08,-0.42 L -0.36,-0.42 Z"),
        [ItemCategories.Consumable] = Icon("M -0.26,-1 L 0.26,-1 L 0.26,-0.42 L 0.7,0.5 C 0.82,0.88 0.5,1 0,1 C -0.5,1 -0.82,0.88 -0.7,0.5 L -0.26,-0.42 Z"),
        [ItemCategories.Unknown] = Icon("M 0,-0.9 L 0.9,0 L 0,0.9 L -0.9,0 Z"),
    };

    private static readonly Geometry DiamondIcon = Icon("M 0,-1 L 1,0 L 0,1 L -1,0 Z");
    private static readonly Geometry FlameIcon = Icon("M 0.05,-1 C 0.5,-0.5 0.92,0 0.62,0.55 C 0.45,0.9 -0.45,0.9 -0.62,0.55 C -0.9,0.05 -0.3,-0.3 0.05,-1 Z");
    private static readonly Geometry FlameCoreIcon = Icon("M 0.03,-0.2 C 0.25,0.05 0.42,0.3 0.28,0.56 C 0.2,0.74 -0.2,0.74 -0.28,0.56 C -0.4,0.32 -0.13,0.12 0.03,-0.2 Z");
    private static readonly Geometry FlameBaseIcon = Icon("M -0.95,0.78 L 0.95,0.78 L 0.8,1 L -0.8,1 Z");

    private static Geometry Icon(string path, bool evenOdd = false)
    {
        var g = Geometry.Parse((evenOdd ? "F0 " : "F1 ") + path).Clone();
        g.Freeze();
        return g;
    }

    private void DrawIcon(DrawingContext dc, Geometry icon, Brush fill, Point at, double size)
    {
        if (_look.IconPlate != null)
        {
            // A dark disc ringed in the category's colour, with the icon inside it.
            dc.DrawEllipse(_look.IconPlate, new Pen(fill, 1.1), at, size, size);
            size *= 0.62;
            dc.PushTransform(new MatrixTransform(size, 0, 0, size, at.X, at.Y));
            dc.DrawGeometry(fill, null, icon);
            dc.Pop();
            return;
        }
        dc.PushTransform(new MatrixTransform(size, 0, 0, size, at.X, at.Y));
        dc.DrawGeometry(fill, MakePen(_look.MarkerOutline, _theme.MarkerOutlineWidth / size), icon);
        dc.Pop();
    }

    private void DrawBonfire(DrawingContext dc, Point at)
    {
        double size = _theme.BonfireIconSize;
        if (!_theme.CategoryIcons)
        {
            dc.DrawEllipse(_look.Bonfire, null, at, size, size);
            dc.DrawEllipse(_look.BonfireCore, null, at, size * 0.39, size * 0.39);
            return;
        }
        dc.PushTransform(new MatrixTransform(size, 0, 0, size, at.X, at.Y));
        var pen = MakePen(_look.MarkerOutline, _theme.MarkerOutlineWidth / size);
        dc.DrawGeometry(_look.Bonfire, pen, FlameBaseIcon);
        dc.DrawGeometry(_look.Bonfire, pen, FlameIcon);
        dc.DrawGeometry(_look.BonfireCore, null, FlameCoreIcon);
        dc.Pop();
    }

    private void DrawItem(DrawingContext dc, string? category, Point at)
    {
        string key = category ?? ItemCategories.Unknown;
        var brush = _look.Category.TryGetValue(key, out var b) ? b : _look.Category.GetValueOrDefault(ItemCategories.Unknown, Brushes.Gray);
        var icon = _theme.CategoryIcons && Icons.TryGetValue(key, out var g) ? g : DiamondIcon;
        DrawIcon(dc, icon, brush, at, _theme.ItemIconSize);
    }

    // ---- map geometry --------------------------------------------------------------------

    /// <summary>
    /// A square piece of one layer in map-pixel space. The map is cut into tiles so that a view
    /// showing a small part of it (the minimap) only has to draw the tiles it can see.
    /// </summary>
    private sealed class Tile
    {
        public Rect Bounds = Rect.Empty;
        public StreamGeometry Fill = new() { FillRule = FillRule.Nonzero };
        public StreamGeometry Edges = new();
        public StreamGeometry Hatch = new();
        public StreamGeometryContext? FillContext, EdgeContext, HatchContext;
    }

    /// <summary>Tile edge in map pixels: 32 m at the default 6 pixels per metre.</summary>
    private const double TileSize = 192;

    private readonly Dictionary<MapData, List<(Vector3 A, Vector3 B)>[]> _outlines = new();
    private MapTransform? _builtFor;
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

    /// <summary>Rebuilds the layer geometry in map-pixel space when the map, the orientation or the theme has changed.</summary>
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
                tile.HatchContext = tile.Hatch.Open();
            }
            return tile;
        }

        float steepUp = MathF.Cos((float)(_theme.SteepDegrees * Math.PI / 180));
        float spacing = (float)Math.Max(0.2, _theme.HatchSpacingMeters);
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

            if (_look.HasHatch)
                AddHatch(tile.HatchContext!, poly.Vertices, t, steepUp, spacing);
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
                tile.HatchContext!.Close();
                tile.Fill.Freeze();
                tile.Edges.Freeze();
                tile.Hatch.Freeze();
            }
            _tiles[layer] = tiles[layer].Values.ToArray();
        }
        _builtFor = t;
    }

    /// <summary>
    /// Step lines across a steep polygon (stairs, a ramp): lines of equal height, one every
    /// <paramref name="spacing"/> metres along the slope, cut to the polygon.
    /// </summary>
    private static void AddHatch(StreamGeometryContext ctx, Vector3[] poly, MapTransform t, float steepUp, float spacing)
    {
        // Newell normal; its horizontal part points down the slope.
        Vector3 n = Vector3.Zero;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        float length = n.Length();
        if (length < 1e-9f || MathF.Abs(n.Y) / length >= steepUp)
            return;   // level enough: no hatching
        float gx = n.X, gz = n.Z, g = MathF.Sqrt(gx * gx + gz * gz);
        if (g < 1e-9f)
            return;
        gx /= g;
        gz /= g;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in poly)
        {
            float s = v.X * gx + v.Z * gz;
            min = MathF.Min(min, s);
            max = MathF.Max(max, s);
        }
        Span<Vector3> hits = stackalloc Vector3[2];
        for (float s = MathF.Ceiling(min / spacing) * spacing; s < max; s += spacing)
        {
            int count = 0;
            for (int i = 0; i < poly.Length && count < 2; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                float sa = a.X * gx + a.Z * gz - s, sb = b.X * gx + b.Z * gz - s;
                if ((sa < 0) == (sb < 0))
                    continue;
                hits[count++] = Vector3.Lerp(a, b, sa / (sa - sb));
            }
            if (count < 2)
                continue;
            var (x0, y0) = t.ToPixel(hits[0]);
            var (x1, y1) = t.ToPixel(hits[1]);
            ctx.BeginFigure(new Point(x0, y0), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(x1, y1), isStroked: true, isSmoothJoin: false);
        }
    }

    // ---- paper ---------------------------------------------------------------------------

    private static ImageBrush? _grain;

    /// <summary>A tile of paper grain, generated once from a fixed seed so every run looks the same.</summary>
    private static ImageBrush Grain()
    {
        if (_grain != null)
            return _grain;
        const int size = 192;
        var pixels = new byte[size * size * 4];
        uint seed = 0x9E3779B9;
        var coarse = new float[(size / 8 + 1) * (size / 8 + 1)];
        for (int i = 0; i < coarse.Length; i++)
        {
            seed = seed * 1664525 + 1013904223;
            coarse[i] = (seed >> 8 & 0xFFFF) / 65535f;
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                seed = seed * 1664525 + 1013904223;
                float fine = (seed >> 8 & 0xFFFF) / 65535f;
                // Fine speckle plus a soft blotch that wraps at the tile edge, so the tiling does not show.
                int w = size / 8;
                float fx = x / 8f, fy = y / 8f;
                int gx = (int)fx, gy = (int)fy;
                float tx = fx - gx, ty = fy - gy;
                float C(int cx, int cy) => coarse[cy % w * (w + 1) + cx % w];
                float blotch = (C(gx, gy) * (1 - tx) + C(gx + 1, gy) * tx) * (1 - ty) + (C(gx, gy + 1) * (1 - tx) + C(gx + 1, gy + 1) * tx) * ty;
                float v = 0.45f * fine + 0.55f * blotch;
                bool dark = v < 0.5f;
                byte alpha = (byte)(Math.Abs(v - 0.5f) * 2 * 150);
                int o = (y * size + x) * 4;
                byte c = dark ? (byte)0 : (byte)255;
                // Premultiplied BGRA.
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(c * alpha / 255);
                pixels[o + 3] = alpha;
            }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        brush.Freeze();
        return _grain = brush;
    }

    // ---- view ----------------------------------------------------------------------------

    /// <summary>Map-pixel → panel matrix for the current view. Updates the view centre while following.</summary>
    public static Matrix ComputeView(Size size, ViewState view, MapTransform t, TrackFrame frame)
    {
        var m = Matrix.Identity;
        if (view.FitToWindow)
        {
            double scale = Math.Min(size.Width / t.Width, size.Height / t.Height) * 0.94;
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

    // ---- drawing -------------------------------------------------------------------------

    /// <summary>Draws one panel into <paramref name="viewport"/>.</summary>
    /// <param name="message">Message for the states that are not plain tracking (game not running, …), or null.</param>
    /// <param name="overlayLines">Debug readout drawn onto the panel (PNG export), or null.</param>
    public void Render(DrawingContext dc, Rect viewport, TrackingSession session, ViewState view, RenderStyle style,
                       string? message = null, IReadOnlyList<string>? overlayLines = null, double pixelsPerDip = 1.0)
    {
        var map = session.Map;
        var t = session.Transform;
        var frame = session.Current;
        var settings = style.Settings;
        var theme = _theme;
        var look = _look;
        EnsureGeometry(map, t);

        var size = viewport.Size;
        var bounds = new Rect(size);
        bool round = style.Minimap && theme.RoundMinimap && !style.Opaque;
        double corner = style.Opaque ? 0 : 10;
        Geometry shape = round
            ? new EllipseGeometry(new Point(size.Width / 2, size.Height / 2), size.Width / 2, size.Height / 2)
            : new RectangleGeometry(bounds, corner, corner);

        dc.PushTransform(new TranslateTransform(viewport.X, viewport.Y));
        if (!style.Opaque)
            dc.PushOpacity(Math.Clamp(style.PanelOpacity, 0.2, 1.0));
        dc.PushClip(shape);
        bool paper = style.Paper && look.Paper != null;
        dc.DrawRectangle(paper ? look.Paper : style.Opaque ? look.Background : look.Panel, null, bounds);

        Matrix m = ComputeView(size, view, t, frame);
        double scale = m.M11;
        int playerLayer = frame.State == TrackState.Tracking ? frame.Layer : session.LastLayer;
        int current = style.ShownLayer is { } forced && forced >= 0 && forced < _tiles.Length ? forced : playerLayer;

        // Only the tiles the panel can see are drawn.
        var toMap = m;
        toMap.Invert();
        var seen = Rect.Transform(bounds, toMap);
        seen.Inflate(8, 8);

        dc.PushTransform(new MatrixTransform(m));
        var otherPen = MakePen(look.OutlineOther, theme.OutlineOtherWidth / scale);
        var hatchPen = look.HasHatch ? MakePen(look.Hatch, theme.HatchWidth / scale) : null;

        // Fills are drawn without edge smoothing: neighbouring tiles then meet exactly, with no
        // hairline between them. The outline drawn on top hides the hard outer edge.
        void DrawFill(int layer, Brush brush)
        {
            var group = new DrawingGroup();
            RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
            using (var g = group.Open())
                foreach (var tile in _tiles[layer])
                    if (tile.Bounds.IntersectsWith(seen))
                        g.DrawGeometry(brush, null, tile.Fill);
            dc.DrawDrawing(group);
        }

        void DrawOther(int layer, double opacity, bool above)
        {
            bool faded = opacity < 0.999;
            if (faded)
                dc.PushOpacity(opacity);
            DrawFill(layer, current < 0 ? look.FloorNeutral : above ? look.FloorAbove : look.FloorOther);
            foreach (var tile in _tiles[layer])
                if (tile.Bounds.IntersectsWith(seen))
                    dc.DrawGeometry(null, otherPen, tile.Edges);
            if (faded)
                dc.Pop();
        }

        // Layers under the shown one first, each step down a little fainter.
        for (int i = 0; i < _tiles.Length; i++)
        {
            if (current >= 0 && i >= current)
                break;
            DrawOther(i, current < 0 ? 1 : Math.Pow(Math.Clamp(theme.LowerLayerFade, 0.2, 1), current - i - 1), above: false);
        }

        if (current >= 0 && current < _tiles.Length)
        {
            // The shown layer: wash, step lines on stairs and ramps, then the ink outline with a soft edge under it.
            var softPen = MakePen(Color.FromRgb(look.Outline.R, look.Outline.G, look.Outline.B), theme.OutlineWidth * 2.6 / scale);
            var inkPen = MakePen(look.Outline, theme.OutlineWidth / scale);
            DrawFill(current, look.Floor);
            if (hatchPen != null)
                foreach (var tile in _tiles[current])
                    if (tile.Bounds.IntersectsWith(seen))
                        dc.DrawGeometry(null, hatchPen, tile.Hatch);
            if (theme.CategoryIcons)
            {
                // Drawn solid and faded as one, so strokes of neighbouring tiles do not add up where they meet.
                dc.PushOpacity(look.Outline.A / 255.0 * 0.25);
                foreach (var tile in _tiles[current])
                    if (tile.Bounds.IntersectsWith(seen))
                        dc.DrawGeometry(null, softPen, tile.Edges);
                dc.Pop();
            }
            foreach (var tile in _tiles[current])
                if (tile.Bounds.IntersectsWith(seen))
                    dc.DrawGeometry(null, inkPen, tile.Edges);

            // Layers above: a faint shadow on what lies below, then the layers themselves.
            if (theme.UpperLayerShadow > 0.01)
            {
                double offset = theme.ShadowOffsetMeters * t.Scale;
                dc.PushOpacity(Math.Clamp(theme.UpperLayerShadow, 0, 1));
                dc.PushTransform(new TranslateTransform(offset, offset));
                for (int i = current + 1; i < _tiles.Length; i++)
                    DrawFill(i, look.Shadow);
                dc.Pop();
                dc.Pop();
            }
            for (int i = current + 1; i < _tiles.Length; i++)
                DrawOther(i, 1, above: true);
        }
        else
        {
            for (int i = 0; i < _tiles.Length; i++)
                DrawOther(i, 1, above: false);
        }
        dc.Pop();

        if (style.Paper && theme.Grain > 0.01)
        {
            dc.PushOpacity(Math.Clamp(theme.Grain, 0, 1) * 0.5);
            dc.DrawRectangle(Grain(), null, bounds);
            dc.Pop();
        }
        if (style.Paper && theme.Vignette > 0.01)
        {
            var vignette = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
            var edge = look.ShadowColour;
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, edge.R, edge.G, edge.B), 0.5));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(Math.Clamp(theme.Vignette, 0, 1) * 215), edge.R, edge.G, edge.B), 1.0));
            dc.DrawRectangle(vignette, null, bounds);
        }

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
            dc.DrawGeometry(null, look.Trail, trail);
        }

        // Markers in panel space so their size does not change with zoom.
        var visible = Rect.Inflate(bounds, 14, 14);
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
                opacity = theme.OtherLayerMarkerOpacity;
            if (visibility == MarkerVisibility.Dimmed)
                opacity *= theme.CollectedMarkerOpacity;
            if (opacity < 1.0)
                dc.PushOpacity(opacity);
            if (marker.IsBonfire)
                DrawBonfire(dc, s);
            else
                DrawItem(dc, marker.Category, s);
            if (opacity < 1.0)
                dc.Pop();

            if (visibility == MarkerVisibility.Shown && shape.FillContains(s))
            {
                float distance = frame.HasPosition ? Vector3.Distance(marker.WorldPosition, frame.World) : 0f;
                drawn.Add((marker, s, distance));
            }
        }

        if (frame.HasPosition)
        {
            var s = m.Transform(new Point(frame.PixelX, frame.PixelY));
            if (frame.State == TrackState.Tracking)
                dc.DrawEllipse(look.Player, look.PlayerRing, s, theme.PlayerSize, theme.PlayerSize);   // smaller than a bonfire marker, so one stays visible underneath
            else
                dc.DrawEllipse(null, look.PlayerLost, s, theme.PlayerSize, theme.PlayerSize);
        }

        if (style.LabelCount != 0)
            DrawLabels(dc, bounds, shape, drawn, style.LabelCount, pixelsPerDip);
        if (style.Legend)
            DrawLegend(dc, bounds, session, settings, pixelsPerDip);
        if (!string.IsNullOrEmpty(style.Title))
        {
            var title = Text(style.Title, round ? theme.TitleSize * 0.8 : theme.TitleSize, look.Text, look.TitleFace, pixelsPerDip);
            // On a disc the title sits centred near the top, where the circle is still wide enough.
            var at = round ? new Point((size.Width - title.Width) / 2, size.Height * 0.075) : new Point(16, 11);
            dc.DrawRoundedRectangle(look.LabelBack, null, new Rect(at.X - 8, at.Y - 3, title.Width + 16, title.Height + 6), 4, 4);
            dc.DrawText(title, at);
        }
        if (!string.IsNullOrEmpty(style.Footer))
        {
            var footer = Text(style.Footer, theme.LegendSize, look.TextMuted, look.LabelFace, pixelsPerDip);
            if (round)
                footer.MaxTextWidth = size.Width * 0.62;
            footer.TextAlignment = round ? TextAlignment.Center : TextAlignment.Left;
            double w = round ? footer.MaxTextWidth : footer.Width;
            var at = round ? new Point((size.Width - w) / 2, size.Height * 0.9 - footer.Height) : new Point(16, size.Height - footer.Height - 13);
            dc.DrawRoundedRectangle(look.LabelBack, null, new Rect(at.X - 8, at.Y - 4, w + 16, footer.Height + 8), 4, 4);
            dc.DrawText(footer, at);
        }

        if (overlayLines is { Count: > 0 })
        {
            var text = Text(string.Join("\n", overlayLines), 13, look.Text, look.MonoFace, pixelsPerDip);
            dc.DrawRectangle(look.LabelBack, null, new Rect(8, 8, text.Width + 20, text.Height + 16));
            dc.DrawText(text, new Point(18, 16));
        }
        if (!string.IsNullOrEmpty(style.Banner))
            DrawCentred(dc, size, style.Banner, style.Minimap ? theme.TitleSize : theme.TitleSize * 1.9, look.TitleFace, look.LabelBack, style.Minimap ? 0.24 : 0.16, pixelsPerDip);
        if (!string.IsNullOrEmpty(message))
            DrawCentred(dc, size, message, Math.Min(theme.LegendSize + 2, Math.Max(11, size.Width / 24)), look.LabelFace, look.Banner, round ? 0.5 : 0.2, pixelsPerDip);

        var borderPen = MakePen(look.PanelBorder, theme.PanelBorderWidth);
        if (!style.Opaque)
        {
            if (round)
                dc.DrawEllipse(null, borderPen, new Point(size.Width / 2, size.Height / 2), size.Width / 2 - 1, size.Height / 2 - 1);
            else
                dc.DrawRoundedRectangle(null, borderPen, new Rect(0.75, 0.75, size.Width - 1.5, size.Height - 1.5), corner, corner);
        }
        dc.Pop();   // clip
        if (!style.Opaque)
            dc.Pop();   // opacity
        dc.Pop();   // translate
    }

    /// <summary>Names the nearest markers. A label that would cover another label, or leave the panel, is left out, so what is shown stays readable.</summary>
    private void DrawLabels(DrawingContext dc, Rect bounds, Geometry shape, List<(MapDocument.MarkerInfo Marker, Point At, float Distance)> markers,
                            int count, double pixelsPerDip)
    {
        var placed = new List<Rect>();
        double gap = Math.Max(_theme.ItemIconSize, 6) + 5;
        int shown = 0;
        foreach (var (marker, at, _) in markers.OrderBy(x => x.Distance))
        {
            if (count >= 0 && shown >= count)
                break;
            string label = marker.Label;
            if (label.Length > 46)
                label = label[..45] + "…";
            var text = Text(label, _theme.LabelSize, _look.Text, _look.LabelFace, pixelsPerDip);
            double w = text.Width + 10, h = text.Height + 4;

            // To the right of the marker; to the left when that would leave the panel.
            var box = new Rect(at.X + gap, at.Y - h / 2, w, h);
            if (!Fits(box))
                box = new Rect(at.X - gap - w, at.Y - h / 2, w, h);
            if (!Fits(box))
                continue;
            var padded = Rect.Inflate(box, 2, 2);
            if (placed.Any(p => p.IntersectsWith(padded)))
                continue;

            placed.Add(box);
            dc.DrawRoundedRectangle(_look.LabelBack, null, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 5, box.Y + 2));
            shown++;
        }

        bool Fits(Rect box) =>
            box.Left >= bounds.Left + 4 && box.Top >= bounds.Top + 4 && box.Right <= bounds.Right - 4 && box.Bottom <= bounds.Bottom - 4 &&
            shape.FillContains(box.TopLeft) && shape.FillContains(box.TopRight) && shape.FillContains(box.BottomLeft) && shape.FillContains(box.BottomRight);
    }

    private void DrawLegend(DrawingContext dc, Rect bounds, TrackingSession session, TrackerSettings settings, double pixelsPerDip)
    {
        var treasures = session.Map.Markers.Where(mk => !mk.IsBonfire && !mk.Unused && mk.Layer >= 0).ToList();
        var rows = new List<(string? Category, string Line)>();
        foreach (string category in ItemCategories.All)
        {
            var inCategory = treasures.Where(mk => (mk.Category ?? ItemCategories.Unknown) == category).ToList();
            if (inCategory.Count == 0)
                continue;
            int left = inCategory.Count(mk => !session.IsCollected(mk));
            string hidden = settings.IsCategoryShown(category) ? "" : " (hidden)";
            rows.Add((category, $"{ItemCategories.DisplayNames[category]}  {left} of {inCategory.Count}{hidden}"));
        }
        rows.Add(("bonfire", "Bonfire"));
        rows.Add(("player", "You"));

        var texts = rows.Select(r => Text(r.Line, _theme.LegendSize, _look.Text, _look.LabelFace, pixelsPerDip)).ToList();
        double lineHeight = Math.Max(texts[0].Height, _theme.ItemIconSize * 2) + 6;
        double width = texts.Max(x => x.Width) + 52, height = rows.Count * lineHeight + 14;
        var box = new Rect(bounds.Right - width - 14, bounds.Bottom - height - 14, width, height);
        dc.DrawRoundedRectangle(_look.LabelBack, null, box, 5, 5);
        for (int i = 0; i < rows.Count; i++)
        {
            double y = box.Y + 8 + i * lineHeight;
            var centre = new Point(box.X + 22, y + lineHeight / 2 - 3);
            switch (rows[i].Category)
            {
                case "bonfire": DrawBonfire(dc, centre); break;
                case "player": dc.DrawEllipse(_look.Player, _look.PlayerRing, centre, _theme.PlayerSize, _theme.PlayerSize); break;
                default: DrawItem(dc, rows[i].Category, centre); break;
            }
            dc.DrawText(texts[i], new Point(box.X + 42, y + (lineHeight - texts[i].Height) / 2 - 3));
        }
    }

    /// <summary>A caption centred horizontally at a fraction of the panel's height.</summary>
    private void DrawCentred(DrawingContext dc, Size size, string caption, double fontSize, Typeface face, Brush back, double at, double pixelsPerDip)
    {
        var text = Text(caption, fontSize, _look.Text, face, pixelsPerDip);
        text.MaxTextWidth = Math.Max(80, size.Width * 0.82);
        text.TextAlignment = TextAlignment.Center;
        double w = Math.Min(text.MaxTextWidth, text.Width + 4) + 28, h = text.Height + 16;
        double x = (size.Width - w) / 2, y = Math.Max(8, size.Height * at - h / 2);
        dc.DrawRoundedRectangle(back, null, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(text, new Point((size.Width - text.MaxTextWidth) / 2, y + 8));
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface face, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, pixelsPerDip);
}
