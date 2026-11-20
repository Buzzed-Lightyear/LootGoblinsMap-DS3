using System.Numerics;

namespace MapBuilder.Pipeline;

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
}
