using System.Numerics;

namespace MapCore;

public enum LocateKind
{
    /// <summary>No polygon contains the point top-down and none is within reach.</summary>
    None,
    /// <summary>The point is inside the polygon in top-down view.</summary>
    Inside,
    /// <summary>Not inside any suitable polygon; this is the nearest one within reach.</summary>
    Nearest,
}

/// <summary>Result of looking up the navmesh polygon under a world position.</summary>
public readonly record struct LocateResult(LocateKind Kind, int PolygonIndex, int Layer, float Distance)
{
    public static readonly LocateResult NotFound = new(LocateKind.None, -1, -1, float.PositiveInfinity);
    public bool Found => Kind != LocateKind.None;
}

/// <summary>
/// Finds the navmesh polygon under the player and with it the current layer.
///
/// Rule (BRIEF-PHASE2): the polygon that contains the point in top-down view with the smallest
/// vertical distance; otherwise the nearest polygon within <see cref="Reach"/> (3 m).
/// One refinement, logged in docs/history/DECISIONS.md: when the best containing polygon is more than
/// <see cref="Reach"/> away vertically (the player is on geometry without navmesh above or
/// below a floor), a polygon within reach in 3D wins over it. With nothing in reach, the
/// containing polygon is still returned and its large distance tells the caller how far off it is.
///
/// Distance is vertical for <see cref="LocateKind.Inside"/> and 3D to the polygon outline for
/// <see cref="LocateKind.Nearest"/>. Layers are stack order, not connected floors, so the layer
/// can only come from the polygon, never from the height alone.
/// </summary>
public sealed class PolygonLocator
{
    public const float DefaultReach = 3f;
    private const float CellSize = 4f;

    private readonly IReadOnlyList<NavPolygon> _polygons;
    private readonly Dictionary<(int, int), List<int>> _grid = new();

    public float Reach { get; }
    public IReadOnlyList<NavPolygon> Polygons => _polygons;

    public PolygonLocator(IReadOnlyList<NavPolygon> polygons, float reach = DefaultReach)
    {
        _polygons = polygons;
        Reach = reach;
        for (int i = 0; i < polygons.Count; i++)
        {
            var p = polygons[i];
            int x0 = Cell(p.MinX), x1 = Cell(p.MaxX), z0 = Cell(p.MinZ), z1 = Cell(p.MaxZ);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_grid.TryGetValue((x, z), out var list))
                        _grid[(x, z)] = list = new List<int>();
                    list.Add(i);
                }
        }
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    public LocateResult Locate(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return LocateResult.NotFound;

        // 1. Polygons containing the point top-down: smallest vertical distance wins.
        int insideIndex = -1;
        float insideDist = float.MaxValue;
        if (_grid.TryGetValue((Cell(position.X), Cell(position.Z)), out var here))
        {
            foreach (int i in here)
            {
                var p = _polygons[i];
                if (position.X < p.MinX || position.X > p.MaxX || position.Z < p.MinZ || position.Z > p.MaxZ)
                    continue;
                if (!Geometry2D.PointInPolyXZ(position, p.Vertices))
                    continue;
                float d = MathF.Abs(position.Y - Geometry2D.HeightAtXZ(p.Vertices, position.X, position.Z));
                if (d < insideDist)
                {
                    insideDist = d;
                    insideIndex = i;
                }
            }
        }
        if (insideIndex >= 0 && insideDist <= Reach)
            return new LocateResult(LocateKind.Inside, insideIndex, _polygons[insideIndex].Layer, insideDist);

        // 2. Otherwise the nearest polygon within reach, measured in 3D to its outline.
        int nearIndex = -1;
        float nearDist = float.MaxValue;
        int cx0 = Cell(position.X - Reach), cx1 = Cell(position.X + Reach);
        int cz0 = Cell(position.Z - Reach), cz1 = Cell(position.Z + Reach);
        for (int cx = cx0; cx <= cx1; cx++)
            for (int cz = cz0; cz <= cz1; cz++)
            {
                if (!_grid.TryGetValue((cx, cz), out var list))
                    continue;
                foreach (int i in list)
                {
                    var p = _polygons[i];
                    if (position.X < p.MinX - Reach || position.X > p.MaxX + Reach ||
                        position.Z < p.MinZ - Reach || position.Z > p.MaxZ + Reach ||
                        position.Y < p.MinY - Reach || position.Y > p.MaxY + Reach)
                        continue;
                    float d = Geometry2D.DistancePointToOutline3D(position, p.Vertices);
                    if (d < nearDist)
                    {
                        nearDist = d;
                        nearIndex = i;
                    }
                }
            }
        if (nearIndex >= 0 && nearDist <= Reach)
            return new LocateResult(LocateKind.Nearest, nearIndex, _polygons[nearIndex].Layer, nearDist);

        // 3. Nothing within reach: fall back to the containing polygon, however far above or below.
        if (insideIndex >= 0)
            return new LocateResult(LocateKind.Inside, insideIndex, _polygons[insideIndex].Layer, insideDist);

        return LocateResult.NotFound;
    }
}
