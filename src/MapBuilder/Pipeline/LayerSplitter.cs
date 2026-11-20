using System.Numerics;

namespace MapBuilder.Pipeline;

public sealed class Layer
{
    public int Index;
    public List<WorldFace> Faces = new();
    public float MinY = float.MaxValue, MaxY = float.MinValue;

    public void Add(WorldFace f)
    {
        Faces.Add(f);
        MinY = Math.Min(MinY, f.MinY);
        MaxY = Math.Max(MaxY, f.MaxY);
    }
}

/// <summary>
/// Splits faces into floor layers such that no layer overlaps itself in top-down view.
/// Faces are processed bottom-up; each face joins the lowest-indexed layer where every
/// face it overlaps in XZ is within the split height (ramps/stairs stay together),
/// otherwise a new layer starts. A spatial hash keeps the overlap test near-linear.
/// </summary>
public static class LayerSplitter
{
    private const float CellSize = 4f;

    public static List<Layer> Split(List<WorldFace> faces, float splitHeight)
    {
        var layers = new List<Layer>();
        var grids = new List<Dictionary<(int, int), List<WorldFace>>>();

        foreach (var face in faces.OrderBy(f => f.CenterY))
        {
            int target = -1;
            for (int li = 0; li < layers.Count && target < 0; li++)
            {
                bool conflict = false;
                foreach (var other in Candidates(grids[li], face))
                {
                    // Same-layer is fine if heights are close (continuous floor/ramp);
                    // a stacked face further than splitHeight above/below must go elsewhere.
                    if (VerticalGap(face, other) > splitHeight &&
                        Geometry2D.ConvexOverlapXZ(face.Vertices, other.Vertices))
                    {
                        conflict = true;
                        break;
                    }
                }
                if (!conflict)
                    target = li;
            }

            if (target < 0)
            {
                layers.Add(new Layer { Index = layers.Count });
                grids.Add(new Dictionary<(int, int), List<WorldFace>>());
                target = layers.Count - 1;
            }

            face.Layer = target;
            layers[target].Add(face);
            Insert(grids[target], face);
        }

        return layers;
    }

    private static float VerticalGap(WorldFace a, WorldFace b)
    {
        // Gap between vertical extents; 0 if they overlap vertically.
        if (a.MaxY < b.MinY) return b.MinY - a.MaxY;
        if (b.MaxY < a.MinY) return a.MinY - b.MaxY;
        return 0f;
    }

    private static IEnumerable<WorldFace> Candidates(Dictionary<(int, int), List<WorldFace>> grid, WorldFace f)
    {
        var seen = new HashSet<WorldFace>();
        foreach (var key in Cells(f))
        {
            if (!grid.TryGetValue(key, out var list))
                continue;
            foreach (var o in list)
                if (seen.Add(o))
                    yield return o;
        }
    }

    private static void Insert(Dictionary<(int, int), List<WorldFace>> grid, WorldFace f)
    {
        foreach (var key in Cells(f))
        {
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<WorldFace>();
            list.Add(f);
        }
    }

    private static IEnumerable<(int, int)> Cells(WorldFace f)
    {
        int x0 = (int)MathF.Floor(f.MinX / CellSize), x1 = (int)MathF.Floor(f.MaxX / CellSize);
        int z0 = (int)MathF.Floor(f.MinZ / CellSize), z1 = (int)MathF.Floor(f.MaxZ / CellSize);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                yield return (x, z);
    }
}
