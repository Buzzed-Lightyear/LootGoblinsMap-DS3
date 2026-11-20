namespace MapBuilder.Pipeline;

/// <summary>
/// Separates the main body of a navmesh from stray pieces far away from it, so that a lone
/// polygon a few hundred metres off does not inflate the map image. The stray polygons stay in
/// the data (and in the polygon file); they just do not count towards the image bounds.
/// </summary>
public static class MainBody
{
    private const float CellSize = 8f;
    /// <summary>Occupied cells this many cells apart (24 m) still belong together.</summary>
    private const int Bridge = 3;

    /// <summary>
    /// Marks stray faces and returns their number. Faces are grouped by top-down proximity; a
    /// group is part of the main body when it holds at least 1 % of the polygons (and at least
    /// eight) or 5 % of the walkable area. The largest group always is.
    /// </summary>
    public static int MarkStrays(List<WorldFace> faces)
    {
        foreach (var f in faces)
            f.Stray = false;
        if (faces.Count == 0)
            return 0;

        // Occupied cells and the faces that touch them.
        var cellFaces = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            int x0 = Cell(f.MinX), x1 = Cell(f.MaxX), z0 = Cell(f.MinZ), z1 = Cell(f.MaxZ);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!cellFaces.TryGetValue((x, z), out var list))
                        cellFaces[(x, z)] = list = new List<int>();
                    list.Add(i);
                }
        }

        // Union-find over faces: faces sharing a cell, or in cells within the bridge distance, are one group.
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[a] = b;
        }

        foreach (var ((cx, cz), list) in cellFaces)
        {
            for (int i = 1; i < list.Count; i++)
                Union(list[0], list[i]);
            for (int dx = 0; dx <= Bridge; dx++)
                for (int dz = dx == 0 ? 1 : -Bridge; dz <= Bridge; dz++)
                    if (cellFaces.TryGetValue((cx + dx, cz + dz), out var other))
                        Union(list[0], other[0]);
        }

        var count = new Dictionary<int, int>();
        var area = new Dictionary<int, double>();
        double totalArea = 0;
        for (int i = 0; i < faces.Count; i++)
        {
            int g = Find(i);
            double a = AreaXZ(faces[i]);
            count[g] = count.GetValueOrDefault(g) + 1;
            area[g] = area.GetValueOrDefault(g) + a;
            totalArea += a;
        }

        int largest = count.OrderByDescending(kv => kv.Value).First().Key;
        int minCount = Math.Max(8, faces.Count / 100);
        var main = count.Keys.Where(g => g == largest || count[g] >= minCount || area[g] >= 0.05 * totalArea).ToHashSet();

        int strays = 0;
        for (int i = 0; i < faces.Count; i++)
        {
            if (main.Contains(Find(i)))
                continue;
            faces[i].Stray = true;
            strays++;
        }
        return strays;
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    private static double AreaXZ(WorldFace f)
    {
        double sum = 0;
        var v = f.Vertices;
        for (int i = 0; i < v.Length; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % v.Length];
            sum += (double)a.X * b.Z - (double)b.X * a.Z;
        }
        return Math.Abs(sum) / 2;
    }
}
