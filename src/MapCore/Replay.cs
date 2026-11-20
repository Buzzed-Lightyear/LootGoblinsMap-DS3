using System.Globalization;
using System.Numerics;

namespace MapCore;

/// <summary>
/// Recorded or synthetic positions for the tracker's replay mode. Text format, one position
/// per line: "x,y,z" (or "t,x,y,z"; the time is ignored). Separators may be commas,
/// semicolons, tabs or spaces. Blank lines and lines starting with '#' are skipped.
/// </summary>
public static class ReplayFile
{
    public static List<Vector3> Read(string path) => Parse(File.ReadLines(path));

    public static List<Vector3> Parse(IEnumerable<string> lines)
    {
        var result = new List<Vector3>();
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (3 or 4))
                throw new InvalidDataException($"Replay line {lineNo}: expected 'x,y,z' or 't,x,y,z', got '{raw}'.");
            int o = parts.Length - 3;
            var v = new Vector3(ParseFloat(parts[o], lineNo), ParseFloat(parts[o + 1], lineNo), ParseFloat(parts[o + 2], lineNo));
            result.Add(v);
        }
        return result;
    }

    private static float ParseFloat(string s, int lineNo) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            ? f
            : throw new InvalidDataException($"Replay line {lineNo}: '{s}' is not a number.");

    public static void Write(string path, IEnumerable<Vector3> positions, string headerComment = null)
    {
        using var w = new StreamWriter(path);
        if (!string.IsNullOrEmpty(headerComment))
            foreach (var line in headerComment.Split('\n'))
                w.WriteLine("# " + line.TrimEnd());
        foreach (var p in positions)
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.X:F3},{p.Y:F3},{p.Z:F3}"));
    }
}

/// <summary>
/// Builds a walkable synthetic path over the navmesh between two world positions (for the
/// replay check): A* over polygons that share a corner, routed through the shared corners,
/// then resampled to even steps.
/// </summary>
public static class NavPath
{
    /// <summary>Polygon vertices closer than this are treated as the same corner when linking polygons.</summary>
    private const float Weld = 0.25f;

    /// <summary>
    /// Returns positions from <paramref name="from"/> to <paramref name="to"/> about
    /// <paramref name="step"/> metres apart. <paramref name="alongNavmesh"/> is false when the two
    /// points are not connected over the mesh and a straight line was used instead.
    /// </summary>
    public static List<Vector3> Between(MapData map, Vector3 from, Vector3 to, float step, out bool alongNavmesh)
    {
        var polys = map.Polygons;
        var start = map.Locator.Locate(from);
        var goal = map.Locator.Locate(to);
        alongNavmesh = false;

        List<Vector3> corners = null;
        if (start.Found && goal.Found)
        {
            var route = FindRoute(polys, start.PolygonIndex, goal.PolygonIndex);
            if (route != null)
            {
                alongNavmesh = true;
                // Go from polygon to polygon through the corners they share (the middle of the shared
                // edge). Each leg then lies inside one convex polygon, so the path stays on the surface
                // on stairs and ramps instead of cutting through the air between polygon centres.
                corners = new List<Vector3> { from };
                for (int i = 1; i < route.Count; i++)
                    corners.Add(Portal(polys[route[i - 1]], polys[route[i]]));
                corners.Add(to);
            }
        }
        corners ??= new List<Vector3> { from, to };
        return Resample(corners, step);
    }

    private static Vector3 Centre(NavPolygon p)
    {
        var c = Vector3.Zero;
        foreach (var v in p.Vertices)
            c += v;
        return c / p.Vertices.Length;
    }

    /// <summary>The middle of the corners two neighbouring polygons have in common.</summary>
    private static Vector3 Portal(NavPolygon a, NavPolygon b)
    {
        var keys = new HashSet<(int, int, int)>();
        foreach (var v in b.Vertices)
            keys.Add(Key(v));
        var sum = Vector3.Zero;
        int n = 0;
        foreach (var v in a.Vertices)
            if (keys.Contains(Key(v)))
            {
                sum += v;
                n++;
            }
        return n > 0 ? sum / n : Centre(a);
    }

    private static (int, int, int) Key(Vector3 v) =>
        ((int)MathF.Round(v.X / Weld), (int)MathF.Round(v.Y / Weld), (int)MathF.Round(v.Z / Weld));

    private static List<int> FindRoute(IReadOnlyList<NavPolygon> polys, int start, int goal)
    {
        // Polygons touching the same welded corner are neighbours.
        var byCorner = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < polys.Count; i++)
            foreach (var v in polys[i].Vertices)
            {
                var k = Key(v);
                if (!byCorner.TryGetValue(k, out var list))
                    byCorner[k] = list = new List<int>();
                list.Add(i);
            }

        var centres = new Vector3[polys.Count];
        for (int i = 0; i < polys.Count; i++)
            centres[i] = Centre(polys[i]);

        var cost = new Dictionary<int, float> { [start] = 0f };
        var cameFrom = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, 0f);
        var closed = new HashSet<int>();

        while (open.TryDequeue(out int current, out _))
        {
            if (current == goal)
            {
                var route = new List<int> { goal };
                while (cameFrom.TryGetValue(route[^1], out int prev))
                    route.Add(prev);
                route.Reverse();
                return route;
            }
            if (!closed.Add(current))
                continue;

            foreach (var v in polys[current].Vertices)
                foreach (int next in byCorner[Key(v)])
                {
                    if (next == current || closed.Contains(next))
                        continue;
                    float g = cost[current] + Vector3.Distance(centres[current], centres[next]);
                    if (cost.TryGetValue(next, out float known) && known <= g)
                        continue;
                    cost[next] = g;
                    cameFrom[next] = current;
                    open.Enqueue(next, g + Vector3.Distance(centres[next], centres[goal]));
                }
        }
        return null;
    }

    public static List<Vector3> Resample(IReadOnlyList<Vector3> corners, float step)
    {
        var result = new List<Vector3> { corners[0] };
        float carried = 0f;
        for (int i = 1; i < corners.Count; i++)
        {
            Vector3 a = corners[i - 1], b = corners[i];
            float length = Vector3.Distance(a, b);
            float at = step - carried;
            while (at <= length)
            {
                result.Add(Vector3.Lerp(a, b, at / length));
                at += step;
            }
            carried = length - (at - step);
        }
        if (Vector3.Distance(result[^1], corners[^1]) > 1e-3f)
            result.Add(corners[^1]);
        return result;
    }
}
