using System.Numerics;

namespace MapCore;

/// <summary>How the map image is turned relative to the world axes. Rotation is in clockwise quarter turns.</summary>
public readonly record struct Orientation(bool FlipX, bool FlipZ, int Rotation)
{
    public static readonly Orientation Default = new(false, false, 0);

    /// <summary>Rotation folded into 0..3.</summary>
    public int QuarterTurns => ((Rotation % 4) + 4) % 4;

    public Orientation Normalized() => new(FlipX, FlipZ, QuarterTurns);

    public override string ToString() => $"flipX={FlipX} flipZ={FlipZ} rotation={QuarterTurns * 90}°";
}

/// <summary>
/// One shared world→pixel transform for the whole map, used by both the builder and the tracker.
/// Axis convention (documented in map.json and docs/map-pipeline.md), applied in this order:
///   1. pixel X = (world X - OriginX) * Scale,  pixel Y = (world Z - OriginZ) * Scale
///   2. FlipX mirrors pixel X, FlipZ mirrors pixel Y (inside the unrotated image)
///   3. the image is rotated clockwise by Rotation quarter turns
/// World Y (height) is not part of the 2D projection.
/// </summary>
public sealed class MapTransform
{
    public double OriginX { get; init; }   // world X mapped to pixel x=0 (before flip/rotation)
    public double OriginZ { get; init; }   // world Z mapped to pixel y=0 (before flip/rotation)
    public double Scale { get; init; }     // pixels per metre

    /// <summary>Image size before rotation.</summary>
    public int BaseWidth { get; init; }
    public int BaseHeight { get; init; }

    public bool FlipX { get; init; }
    public bool FlipZ { get; init; }

    /// <summary>Clockwise quarter turns, 0..3.</summary>
    public int Rotation { get; init; }

    /// <summary>Image size after rotation (what a renderer allocates).</summary>
    public int Width => (Rotation & 1) == 0 ? BaseWidth : BaseHeight;
    public int Height => (Rotation & 1) == 0 ? BaseHeight : BaseWidth;

    public Orientation Orientation => new(FlipX, FlipZ, Rotation);

    public static MapTransform FitBounds(float minX, float maxX, float minZ, float maxZ,
                                         double pixelsPerMeter, double padMeters, bool flipX, bool flipZ,
                                         int rotation = 0)
    {
        double ox = minX - padMeters;
        double oz = minZ - padMeters;
        int w = (int)Math.Ceiling((maxX - minX + 2 * padMeters) * pixelsPerMeter);
        int h = (int)Math.Ceiling((maxZ - minZ + 2 * padMeters) * pixelsPerMeter);
        return new MapTransform
        {
            OriginX = ox, OriginZ = oz, Scale = pixelsPerMeter,
            BaseWidth = Math.Max(w, 1), BaseHeight = Math.Max(h, 1),
            FlipX = flipX, FlipZ = flipZ,
            Rotation = new Orientation(flipX, flipZ, rotation).QuarterTurns,
        };
    }

    /// <summary>The same map bounds and scale, turned a different way.</summary>
    public MapTransform WithOrientation(Orientation o) => new()
    {
        OriginX = OriginX, OriginZ = OriginZ, Scale = Scale,
        BaseWidth = BaseWidth, BaseHeight = BaseHeight,
        FlipX = o.FlipX, FlipZ = o.FlipZ, Rotation = o.QuarterTurns,
    };

    public (double X, double Y) ToPixel(Vector3 world) => ToPixel(world.X, world.Z);

    public (double X, double Y) ToPixel(double worldX, double worldZ)
    {
        double px = (worldX - OriginX) * Scale;
        double py = (worldZ - OriginZ) * Scale;
        if (FlipX) px = BaseWidth - px;
        if (FlipZ) py = BaseHeight - py;
        return Rotation switch
        {
            1 => (BaseHeight - py, px),
            2 => (BaseWidth - px, BaseHeight - py),
            3 => (py, BaseWidth - px),
            _ => (px, py),
        };
    }

    public (double X, double Z) ToWorld(double px, double py)
    {
        (px, py) = Rotation switch
        {
            1 => (py, BaseHeight - px),
            2 => (BaseWidth - px, BaseHeight - py),
            3 => (BaseWidth - py, px),
            _ => (px, py),
        };
        if (FlipX) px = BaseWidth - px;
        if (FlipZ) py = BaseHeight - py;
        return (px / Scale + OriginX, py / Scale + OriginZ);
    }
}
