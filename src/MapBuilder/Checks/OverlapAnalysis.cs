using System.Globalization;
using System.Text;
using MapBuilder.Pipeline;
using MapCore;

namespace MapBuilder.Checks;

/// <summary>Two maps whose navmeshes occupy the same space.</summary>
public sealed record MapOverlap(string MapA, string MapB, int PolygonsOfA, int PolygonsOfB, double ShareOfA, double ShareOfB);

/// <summary>
/// The world-space question: taking every map's navmesh in its own coordinates, do any two maps
/// occupy the same space? Two polygons from different maps "share space" when they overlap in
/// top-down view and are within <see cref="VerticalReach"/> of each other vertically, which is
/// what would make a position ambiguous between the two maps.
/// </summary>
public static class OverlapAnalysis
{
    public const float VerticalReach = 3f;
    private const float CellSize = 8f;

    public static List<MapOverlap> Run(IReadOnlyDictionary<string, List<WorldFace>> maps)
    {
        var ids = maps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var faces = ids.Select(id => maps[id]).ToList();

        // Cell → (map index, face index) for every face of every map.
        var grid = new Dictionary<(int, int), List<(int Map, int Face)>>();
        for (int m = 0; m < faces.Count; m++)
            for (int f = 0; f < faces[m].Count; f++)
            {
                var face = faces[m][f];
                int x0 = Cell(face.MinX), x1 = Cell(face.MaxX), z0 = Cell(face.MinZ), z1 = Cell(face.MaxZ);
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!grid.TryGetValue((x, z), out var list))
                            grid[(x, z)] = list = new List<(int, int)>();
                        list.Add((m, f));
                    }
            }

        // For each ordered pair of maps, the faces of the first that share space with the second.
        var shared = new Dictionary<(int, int), HashSet<int>>();
        foreach (var list in grid.Values)
        {
            if (list.Count < 2)
                continue;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var (ma, fa) = list[i];
                    var (mb, fb) = list[j];
                    if (ma == mb)
                        continue;
                    var a = faces[ma][fa];
                    var b = faces[mb][fb];
                    if (a.MaxX < b.MinX || b.MaxX < a.MinX || a.MaxZ < b.MinZ || b.MaxZ < a.MinZ)
                        continue;
                    if (a.MinY > b.MaxY + VerticalReach || b.MinY > a.MaxY + VerticalReach)
                        continue;
                    if (!Geometry2D.ConvexOverlapXZ(a.Vertices, b.Vertices))
                        continue;
                    Mark(ma, mb, fa);
                    Mark(mb, ma, fb);
                }
        }

        void Mark(int from, int to, int face)
        {
            if (!shared.TryGetValue((from, to), out var set))
                shared[(from, to)] = set = new HashSet<int>();
            set.Add(face);
        }

        var result = new List<MapOverlap>();
        for (int a = 0; a < ids.Count; a++)
            for (int b = a + 1; b < ids.Count; b++)
            {
                int ofA = shared.TryGetValue((a, b), out var sa) ? sa.Count : 0;
                int ofB = shared.TryGetValue((b, a), out var sb) ? sb.Count : 0;
                if (ofA == 0 && ofB == 0)
                    continue;
                result.Add(new MapOverlap(ids[a], ids[b], ofA, ofB,
                    100.0 * ofA / Math.Max(1, faces[a].Count), 100.0 * ofB / Math.Max(1, faces[b].Count)));
            }
        return result.OrderByDescending(o => o.PolygonsOfA + o.PolygonsOfB).ToList();
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    public static string Describe(IReadOnlyList<MapOverlap> overlaps, int mapCount, IReadOnlyDictionary<string, string> names)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        int pairs = mapCount * (mapCount - 1) / 2;
        if (overlaps.Count == 0)
        {
            sb.AppendLine($"None of the {pairs} map pairs share space: every position belongs to at most one map, so position alone could identify the map.");
            return sb.ToString();
        }

        sb.AppendLine(string.Create(c, $"{overlaps.Count} of {pairs} map pairs share space (polygons overlapping top-down within {VerticalReach:F0} m of height), so a position alone cannot identify the map."));
        sb.AppendLine();
        sb.AppendLine("| Map A | Map B | Polygons of A in B's space | Polygons of B in A's space |");
        sb.AppendLine("|---|---|---|---|");
        string Name(string id) => names.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? $"{id} {n}" : id;
        foreach (var o in overlaps)
            sb.AppendLine(string.Create(c, $"| {Name(o.MapA)} | {Name(o.MapB)} | {o.PolygonsOfA} ({o.ShareOfA:F1} %) | {o.PolygonsOfB} ({o.ShareOfB:F1} %) |"));
        return sb.ToString();
    }
}
