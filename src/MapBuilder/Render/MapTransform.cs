using System.Numerics;

namespace MapBuilder.Render;

/// <summary>
/// One shared world→pixel transform for the whole map.
/// Axis convention (documented in map.json and REPORT.md):
///   pixel X = (world X - OriginX) * Scale   (negated around the image when FlipX)
///   pixel Y = (world Z - OriginZ) * Scale   (negated around the image when FlipZ)
/// World Y (height) is not part of the 2D projection. Orientation against the in-game
/// compass is unverified; FlipX/FlipZ in the config are the single switch to mirror.
/// </summary>
public sealed class MapTransform
{
    public double OriginX { get; init; }   // world X mapped to pixel x=0 (before flip)
    public double OriginZ { get; init; }   // world Z mapped to pixel y=0 (before flip)
    public double Scale { get; init; }     // pixels per metre
    public int Width { get; init; }
    public int Height { get; init; }
    public bool FlipX { get; init; }
    public bool FlipZ { get; init; }

    public static MapTransform FitBounds(float minX, float maxX, float minZ, float maxZ,
                                         double pixelsPerMeter, double padMeters, bool flipX, bool flipZ)
    {
        double ox = minX - padMeters;
        double oz = minZ - padMeters;
        int w = (int)Math.Ceiling((maxX - minX + 2 * padMeters) * pixelsPerMeter);
        int h = (int)Math.Ceiling((maxZ - minZ + 2 * padMeters) * pixelsPerMeter);
        return new MapTransform
        {
            OriginX = ox, OriginZ = oz, Scale = pixelsPerMeter,
            Width = Math.Max(w, 1), Height = Math.Max(h, 1),
            FlipX = flipX, FlipZ = flipZ,
        };
    }

    public (double X, double Y) ToPixel(Vector3 world) => ToPixel(world.X, world.Z);

    public (double X, double Y) ToPixel(double worldX, double worldZ)
    {
        double px = (worldX - OriginX) * Scale;
        double py = (worldZ - OriginZ) * Scale;
        if (FlipX) px = Width - px;
        if (FlipZ) py = Height - py;
        return (px, py);
    }

    public (double X, double Z) ToWorld(double px, double py)
    {
        if (FlipX) px = Width - px;
        if (FlipZ) py = Height - py;
        return (px / Scale + OriginX, py / Scale + OriginZ);
    }
}
