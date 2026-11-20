using System.Numerics;

namespace MapCore;

/// <summary>2D (top-down, XZ plane) geometry helpers.</summary>
public static class Geometry2D
{
    /// <summary>Convex polygon overlap in the XZ plane via the separating axis theorem.
    /// Touching at an edge/point does not count as overlap (small epsilon).</summary>
    public static bool ConvexOverlapXZ(Vector3[] a, Vector3[] b, float epsilon = 1e-4f)
    {
        return !HasSeparatingAxis(a, b, epsilon) && !HasSeparatingAxis(b, a, epsilon);
    }

    private static bool HasSeparatingAxis(Vector3[] a, Vector3[] b, float epsilon)
    {
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            var q = a[(i + 1) % a.Length];
            // Edge in XZ; axis = perpendicular.
            float ax = -(q.Z - p.Z);
            float az = q.X - p.X;
            float len = MathF.Sqrt(ax * ax + az * az);
            if (len < 1e-9f)
                continue;
            ax /= len; az /= len;

            (float min, float max) ra = Project(a, ax, az);
            (float min, float max) rb = Project(b, ax, az);
            if (ra.max <= rb.min + epsilon || rb.max <= ra.min + epsilon)
                return true;
        }
        return false;
    }

    private static (float, float) Project(Vector3[] poly, float ax, float az)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in poly)
        {
            float d = v.X * ax + v.Z * az;
            if (d < min) min = d;
            if (d > max) max = d;
        }
        return (min, max);
    }

    /// <summary>Horizontal (XZ) distance from a point to a polygon; 0 when inside.</summary>
    public static float DistancePointToPolyXZ(Vector3 p, Vector3[] poly)
    {
        if (PointInPolyXZ(p, poly))
            return 0f;
        float best = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            best = Math.Min(best, DistancePointToSegmentXZ(p, a, b));
        }
        return best;
    }

    public static bool PointInPolyXZ(Vector3 p, Vector3[] poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if (poly[i].Z > p.Z != poly[j].Z > p.Z &&
                p.X < (poly[j].X - poly[i].X) * (p.Z - poly[i].Z) / (poly[j].Z - poly[i].Z) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    public static float DistancePointToSegmentXZ(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, abz = b.Z - a.Z;
        float apx = p.X - a.X, apz = p.Z - a.Z;
        float lenSq = abx * abx + abz * abz;
        float t = lenSq < 1e-12f ? 0f : Math.Clamp((apx * abx + apz * abz) / lenSq, 0f, 1f);
        float dx = apx - t * abx, dz = apz - t * abz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Height (world Y) of the polygon's surface at a top-down position, from the plane through
    /// its vertices (Newell normal, so it also behaves for slightly non-planar polygons).
    /// Falls back to the mean vertex height when the polygon is vertical or degenerate.
    /// </summary>
    public static float HeightAtXZ(Vector3[] poly, float x, float z)
    {
        Vector3 normal = Vector3.Zero, centre = Vector3.Zero;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            normal.X += (a.Y - b.Y) * (a.Z + b.Z);
            normal.Y += (a.Z - b.Z) * (a.X + b.X);
            normal.Z += (a.X - b.X) * (a.Y + b.Y);
            centre += a;
        }
        centre /= poly.Length;
        if (MathF.Abs(normal.Y) < 1e-6f)
            return centre.Y;
        return centre.Y - (normal.X * (x - centre.X) + normal.Z * (z - centre.Z)) / normal.Y;
    }

    /// <summary>
    /// Top-down distance from a point to the nearest point of the polygon's outline, and the
    /// outline's height (world Y) at that nearest point.
    /// </summary>
    public static float ClosestOutlinePointXZ(Vector3 p, Vector3[] poly, out float height)
    {
        float best = float.MaxValue;
        height = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            float abx = b.X - a.X, abz = b.Z - a.Z;
            float lenSq = abx * abx + abz * abz;
            float t = lenSq < 1e-12f ? 0f : Math.Clamp(((p.X - a.X) * abx + (p.Z - a.Z) * abz) / lenSq, 0f, 1f);
            float dx = p.X - (a.X + t * abx), dz = p.Z - (a.Z + t * abz);
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < best)
            {
                best = dist;
                height = a.Y + t * (b.Y - a.Y);
            }
        }
        return best;
    }

    /// <summary>Shortest 3D distance from a point to the polygon's outline.</summary>
    public static float DistancePointToOutline3D(Vector3 p, Vector3[] poly)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var ab = poly[(i + 1) % poly.Length] - a;
            float lenSq = ab.LengthSquared();
            float t = lenSq < 1e-12f ? 0f : Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f);
            best = Math.Min(best, Vector3.Distance(p, a + t * ab));
        }
        return best;
    }

    /// <summary>
    /// Outline of a set of polygons in top-down view: the edges that belong to exactly one
    /// polygon (end points matched to 1 cm). Used for the layer outlines by builder and tracker.
    /// </summary>
    public static List<(Vector3 A, Vector3 B)> OutlineEdgesXZ(IEnumerable<Vector3[]> polygons)
    {
        var counts = new Dictionary<(long, long, long, long), (Vector3 A, Vector3 B, int N)>();
        foreach (var poly in polygons)
        {
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Length];
                var ka = ((long)MathF.Round(a.X * 100), (long)MathF.Round(a.Z * 100));
                var kb = ((long)MathF.Round(b.X * 100), (long)MathF.Round(b.Z * 100));
                var key = ka.CompareTo(kb) <= 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2)
                                                : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
                counts[key] = counts.TryGetValue(key, out var e) ? (e.A, e.B, e.N + 1) : (a, b, 1);
            }
        }
        var result = new List<(Vector3, Vector3)>();
        foreach (var (_, e) in counts)
            if (e.N == 1)
                result.Add((e.A, e.B));
        return result;
    }
}
