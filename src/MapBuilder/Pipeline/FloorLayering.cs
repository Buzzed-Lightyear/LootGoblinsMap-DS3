using System.Numerics;
using MapCore;

namespace MapBuilder.Pipeline;

/// <summary>
/// Splits the navmesh into layers that follow connected floors.
///
/// The older <see cref="LayerSplitter"/> decides every polygon on its own ("lowest layer where
/// nothing under or over me is too far away"), so one storey changes layer wherever something
/// happens to lie beneath it, even between two polygons that share an edge. This splitter
/// first works out what is connected and only then assigns layers, a whole floor at a time:
///
///  1. Links: two polygons are linked when their outlines touch in top-down view (within
///     <see cref="Options.TouchDistance"/>, or one reaches into the other) and the height step
///     there is small. This also joins separately placed navmesh pieces that share no corners.
///  2. Flat patches: near-level polygons joined by links of at most <see cref="Options.JoinStep"/>
///     form one patch (a floor, a landing, a bridge deck). A patch is never cut apart later.
///     Steeper polygons (stairs, ramps) stay single.
///  3. Regions: starting from the lowest unclaimed piece and always taking the lowest reachable
///     one next, pieces are added as long as the region does not come to lie over itself
///     (top-down overlap with more than <see cref="Options.SplitHeight"/> between). The cut
///     therefore falls on the stairs, not in the middle of a floor.
///  4. Layers: regions that overlap each other never share a layer; among the layers that
///     remain open to it, a region goes where the regions it touches are, so that as few
///     small steps as possible cross from one layer to another.
///  5. Refinement: every single piece (a level patch as a whole, or one stair/ramp polygon)
///     may still move to the layer most of its neighbours are on, if nothing on that layer
///     lies over or under it. Layers are then numbered bottom-up by mean height.
///
/// The invariant of the old splitter still holds: no layer overlaps itself in top-down view
/// with more than the split height in between.
/// </summary>
public static class FloorLayering
{
    public sealed class Options
    {
        /// <summary>Stacked faces further apart than this can never share a layer (same value as before).</summary>
        public float SplitHeight = 1.8f;
        /// <summary>Largest height step across which two level areas still count as one floor.</summary>
        public float JoinStep = 0.6f;
        /// <summary>How close two outlines must come in top-down view to count as touching.</summary>
        public float TouchDistance = 0.3f;
        /// <summary>Polygons no steeper than this are "level"; steeper ones are stairs or ramps.</summary>
        public float FlatSlopeDegrees = 15f;
    }

    public sealed class Result
    {
        public List<Layer> Layers;
        public int Links, FlatPatches, Regions;
        /// <summary>Small-step links whose two polygons ended up on different layers.</summary>
        public int Seams;
        /// <summary>The same count after the region step, before single pieces were moved.</summary>
        public int SeamsBeforeRefining;
        /// <summary>Seams by what meets there: two level polygons, a level one and a stair/ramp, or two stair/ramp polygons.</summary>
        public int SeamsLevelToLevel, SeamsLevelToSteep, SeamsSteepToSteep;
    }

    private const float CellSize = 4f;

    public static Result Split(List<WorldFace> faces, Options options)
    {
        int n = faces.Count;
        var links = BuildLinks(faces, options.TouchDistance, options.SplitHeight);

        // ---- 2. flat patches -------------------------------------------------------------
        var flat = new bool[n];
        float minUp = MathF.Cos(options.FlatSlopeDegrees * MathF.PI / 180f);
        for (int i = 0; i < n; i++)
            flat[i] = UpComponent(faces[i].Vertices) >= minUp;

        var unitOf = new int[n];
        Array.Fill(unitOf, -1);
        var units = new List<List<int>>();
        int flatPatches = 0;

        var triedBy = new int[n];           // stamp: last patch/region that looked at this item
        Array.Fill(triedBy, -1);
        foreach (int seed in Enumerable.Range(0, n).Where(i => flat[i]).OrderBy(i => faces[i].CenterY))
        {
            if (unitOf[seed] >= 0)
                continue;
            int id = units.Count;
            var members = new List<int>();
            var set = new FaceSet(faces, options.SplitHeight);
            var queue = new PriorityQueue<int, float>();
            queue.Enqueue(seed, faces[seed].CenterY);
            while (queue.TryDequeue(out int f, out _))
            {
                if (unitOf[f] >= 0 || triedBy[f] == id)
                    continue;
                triedBy[f] = id;
                if (set.ConflictsWith(f))
                    continue;           // would put the patch over itself: leave it for another patch
                unitOf[f] = id;
                members.Add(f);
                set.Add(f);
                foreach (var (other, step) in links[f])
                    if (flat[other] && unitOf[other] < 0 && step <= options.JoinStep)
                        queue.Enqueue(other, faces[other].CenterY);
            }
            units.Add(members);
            flatPatches++;
        }
        for (int i = 0; i < n; i++)
        {
            if (unitOf[i] >= 0)
                continue;
            unitOf[i] = units.Count;
            units.Add(new List<int> { i });
        }

        // Unit heights and unit-to-unit links.
        int unitCount = units.Count;
        var unitHeight = new float[unitCount];
        var unitLinks = new HashSet<int>[unitCount];
        for (int u = 0; u < unitCount; u++)
        {
            unitHeight[u] = units[u].Average(f => faces[f].CenterY);
            unitLinks[u] = new HashSet<int>();
        }
        for (int i = 0; i < n; i++)
            foreach (var (other, _) in links[i])
                if (unitOf[other] != unitOf[i])
                    unitLinks[unitOf[i]].Add(unitOf[other]);

        // ---- 3. regions ------------------------------------------------------------------
        var regionOf = new int[unitCount];
        Array.Fill(regionOf, -1);
        var unitTriedBy = new int[unitCount];
        Array.Fill(unitTriedBy, -1);
        var regions = new List<List<int>>();    // faces per region, in seed (bottom-up) order
        foreach (int seed in Enumerable.Range(0, unitCount).OrderBy(u => unitHeight[u]))
        {
            if (regionOf[seed] >= 0)
                continue;
            int id = regions.Count;
            var members = new List<int>();
            var set = new FaceSet(faces, options.SplitHeight);
            var queue = new PriorityQueue<int, float>();
            queue.Enqueue(seed, unitHeight[seed]);
            while (queue.TryDequeue(out int u, out _))
            {
                if (regionOf[u] >= 0 || unitTriedBy[u] == id)
                    continue;
                unitTriedBy[u] = id;
                if (units[u].Any(set.ConflictsWith))
                    continue;           // the region would lie over itself here: this is where it ends
                regionOf[u] = id;
                foreach (int f in units[u])
                {
                    members.Add(f);
                    set.Add(f);
                }
                foreach (int other in unitLinks[u])
                    if (regionOf[other] < 0)
                        queue.Enqueue(other, unitHeight[other]);
            }
            regions.Add(members);
        }

        // ---- 4. layers -------------------------------------------------------------------
        int regionCount = regions.Count;
        var regionOfFace = new int[n];
        var everything = new FaceSet(faces, options.SplitHeight);
        for (int r = 0; r < regionCount; r++)
            foreach (int f in regions[r])
            {
                regionOfFace[f] = r;
                everything.Add(f);
            }

        // Which regions lie over or under each other (and so can never share a layer) …
        var overlapping = new HashSet<int>[regionCount];
        // … and how strongly regions touch: the number of small-step links between them.
        var touching = new Dictionary<int, int>[regionCount];
        for (int r = 0; r < regionCount; r++)
        {
            overlapping[r] = new HashSet<int>();
            touching[r] = new Dictionary<int, int>();
        }
        for (int f = 0; f < n; f++)
        {
            int r = regionOfFace[f];
            foreach (int g in everything.Conflicts(f))
                overlapping[r].Add(regionOfFace[g]);
            foreach (var (other, step) in links[f])
            {
                int o = regionOfFace[other];
                if (o != r && step < options.JoinStep)
                    touching[r][o] = touching[r].GetValueOrDefault(o) + 1;
            }
        }

        // Start bottom-up: each region on the lowest layer that holds nothing it overlaps.
        var layerOf = new int[regionCount];
        int layerCount = 0;
        for (int r = 0; r < regionCount; r++)
        {
            int layer = 0;
            while (overlapping[r].Any(o => o < r && layerOf[o] == layer))
                layer++;
            layerOf[r] = layer;
            layerCount = Math.Max(layerCount, layer + 1);
        }

        // Then pull regions that touch onto the same layer wherever nothing they overlap is in the
        // way. Every move raises the number of touching links that stay within one layer, so this
        // ends; what is left are seams between regions that overlap and have to differ.
        var order = Enumerable.Range(0, regionCount).OrderByDescending(r => regions[r].Count).ToArray();
        for (int round = 0, moved = 1; moved > 0 && round < 200; round++)
        {
            moved = 0;
            foreach (int r in order)
            {
                var gain = new int[layerCount];
                foreach (var (o, weight) in touching[r])
                    gain[layerOf[o]] += weight;
                int best = layerOf[r];
                for (int layer = 0; layer < layerCount; layer++)
                    if (gain[layer] > gain[best] && !overlapping[r].Any(o => layerOf[o] == layer))
                        best = layer;
                if (best != layerOf[r])
                {
                    layerOf[r] = best;
                    moved++;
                }
            }
        }

        // Refine piece by piece. A region can reach round a floor it had to leave out and pick up
        // the odd sloped polygon lying in it, and a flight of stairs can end up cut in several
        // places. So let every single piece (a level patch as a whole, or one stair/ramp polygon)
        // move to the layer most of its small-step neighbours are on, as long as it does not
        // come to lie over or under anything on that layer. Each move removes seams, so this ends.
        var faceLayer = new int[n];
        for (int f = 0; f < n; f++)
            faceLayer[f] = layerOf[regionOfFace[f]];
        int seamsBeforeRefining = CountSeamLinks();
        var unitOrder = Enumerable.Range(0, unitCount).OrderBy(u => units[u].Count).ToArray();
        for (int round = 0, moved = 1; moved > 0 && round < 200; round++)
        {
            moved = 0;
            foreach (int u in unitOrder)
            {
                int current = faceLayer[units[u][0]];
                var gain = new int[layerCount];
                foreach (int f in units[u])
                    foreach (var (other, step) in links[f])
                        if (step < options.JoinStep && unitOf[other] != u)
                            gain[faceLayer[other]]++;

                int best = current;
                for (int layer = 0; layer < layerCount; layer++)
                {
                    if (gain[layer] <= gain[best])
                        continue;
                    int candidate = layer;
                    if (!units[u].Any(f => everything.Conflicts(f).Any(g => faceLayer[g] == candidate)))
                        best = layer;
                }
                if (best == current)
                    continue;
                foreach (int f in units[u])
                    faceLayer[f] = best;
                moved++;
            }
        }

        int CountSeamLinks()
        {
            int count = 0;
            for (int f = 0; f < n; f++)
                foreach (var (other, step) in links[f])
                    if (other > f && step < options.JoinStep && faceLayer[f] != faceLayer[other])
                        count++;
            return count;
        }

        // Number the layers bottom-up by the mean height of what they hold; drop empty ones.
        var heightSum = new double[layerCount];
        var faceCount = new int[layerCount];
        for (int f = 0; f < n; f++)
        {
            heightSum[faceLayer[f]] += faces[f].CenterY;
            faceCount[faceLayer[f]]++;
        }
        var renumber = new int[layerCount];
        int used = 0;
        foreach (int layer in Enumerable.Range(0, layerCount).Where(l => faceCount[l] > 0).OrderBy(l => heightSum[l] / faceCount[l]))
            renumber[layer] = used++;

        var layers = new List<Layer>();
        for (int i = 0; i < used; i++)
            layers.Add(new Layer { Index = i });
        for (int f = 0; f < n; f++)
        {
            int layer = renumber[faceLayer[f]];
            faces[f].Layer = layer;
            layers[layer].Add(faces[f]);
        }

        // What is left, by what meets there.
        int levelLevel = 0, levelSteep = 0, steepSteep = 0;
        for (int f = 0; f < n; f++)
            foreach (var (other, step) in links[f])
            {
                if (other < f || step >= options.JoinStep || faceLayer[f] == faceLayer[other])
                    continue;
                if (flat[f] && flat[other]) levelLevel++;
                else if (flat[f] || flat[other]) levelSteep++;
                else steepSteep++;
            }

        return new Result
        {
            Layers = layers,
            Links = links.Sum(l => l.Count) / 2,
            FlatPatches = flatPatches,
            Regions = regionCount,
            SeamsBeforeRefining = seamsBeforeRefining,
            Seams = levelLevel + levelSteep + steepSteep,
            SeamsLevelToLevel = levelLevel,
            SeamsLevelToSteep = levelSteep,
            SeamsSteepToSteep = steepSteep,
        };
    }

    /// <summary>
    /// Counts seams: linked polygon pairs with a step below <paramref name="maxStep"/> that ended
    /// up on different layers. This is what shows up in the tracker as the highlighted floor
    /// changing over a tiny step. Works for any layering, so the two splitters can be compared.
    /// </summary>
    public static int CountSeams(List<WorldFace> faces, float maxStep, float touchDistance = 0.3f)
    {
        var links = BuildLinks(faces, touchDistance, maxStep);
        int seams = 0;
        for (int i = 0; i < faces.Count; i++)
            foreach (var (other, step) in links[i])
                if (other > i && step < maxStep && faces[i].Layer != faces[other].Layer)
                    seams++;
        return seams;
    }

    /// <summary>
    /// Counts pairs of faces on the same layer that overlap in top-down view with more than the
    /// split height between them. Must be zero for any valid layering.
    /// </summary>
    public static int CountSelfOverlaps(List<WorldFace> faces, float splitHeight)
    {
        var all = new FaceSet(faces, splitHeight);
        for (int f = 0; f < faces.Count; f++)
            all.Add(f);
        int pairs = 0;
        for (int f = 0; f < faces.Count; f++)
            foreach (int g in all.Conflicts(f))
                if (g > f && faces[g].Layer == faces[f].Layer)
                    pairs++;
        return pairs;
    }

    /// <summary>For every face, the faces it touches in top-down view with a height step of at most <paramref name="maxStep"/>.</summary>
    private static List<(int Other, float Step)>[] BuildLinks(List<WorldFace> faces, float touch, float maxStep)
    {
        int n = faces.Count;
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
            foreach (var key in Cells(faces[i], 0))
            {
                if (!grid.TryGetValue(key, out var list))
                    grid[key] = list = new List<int>();
                list.Add(i);
            }

        var best = new Dictionary<(int, int), float>();
        for (int i = 0; i < n; i++)
        {
            var verts = faces[i].Vertices;
            for (int e = 0; e < verts.Length; e++)
            {
                var a = verts[e];
                var mid = (a + verts[(e + 1) % verts.Length]) * 0.5f;
                Probe(i, a);
                Probe(i, mid);
            }
        }

        void Probe(int i, Vector3 s)
        {
            int x0 = Cell(s.X - touch), x1 = Cell(s.X + touch), z0 = Cell(s.Z - touch), z1 = Cell(s.Z + touch);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!grid.TryGetValue((cx, cz), out var list))
                        continue;
                    foreach (int j in list)
                    {
                        if (j == i)
                            continue;
                        var o = faces[j];
                        if (s.X < o.MinX - touch || s.X > o.MaxX + touch || s.Z < o.MinZ - touch || s.Z > o.MaxZ + touch ||
                            s.Y < o.MinY - maxStep || s.Y > o.MaxY + maxStep)
                            continue;

                        float step;
                        if (Geometry2D.PointInPolyXZ(s, o.Vertices))
                        {
                            // One piece reaches into the other (navmesh pieces overlap at their seams).
                            step = MathF.Abs(s.Y - Geometry2D.HeightAtXZ(o.Vertices, s.X, s.Z));
                        }
                        else
                        {
                            float dist = Geometry2D.ClosestOutlinePointXZ(s, o.Vertices, out float y);
                            if (dist > touch)
                                continue;
                            step = MathF.Abs(s.Y - y);
                        }
                        if (step > maxStep)
                            continue;

                        var key = i < j ? (i, j) : (j, i);
                        if (!best.TryGetValue(key, out float known) || step < known)
                            best[key] = step;
                    }
                }
        }

        var links = new List<(int, float)>[n];
        for (int i = 0; i < n; i++)
            links[i] = new List<(int, float)>();
        foreach (var ((i, j), step) in best)
        {
            links[i].Add((j, step));
            links[j].Add((i, step));
        }
        return links;
    }

    /// <summary>How much the polygon's normal points up: 1 for a level floor, 0 for a wall.</summary>
    private static float UpComponent(Vector3[] poly)
    {
        Vector3 normal = Vector3.Zero;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            normal.X += (a.Y - b.Y) * (a.Z + b.Z);
            normal.Y += (a.Z - b.Z) * (a.X + b.X);
            normal.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        float length = normal.Length();
        return length < 1e-9f ? 0f : MathF.Abs(normal.Y) / length;
    }

    private static int Cell(float v) => (int)MathF.Floor(v / CellSize);

    private static IEnumerable<(int, int)> Cells(WorldFace f, float margin)
    {
        int x0 = Cell(f.MinX - margin), x1 = Cell(f.MaxX + margin);
        int z0 = Cell(f.MinZ - margin), z1 = Cell(f.MaxZ + margin);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                yield return (x, z);
    }

    /// <summary>A growing set of faces that can say which of its members a face would be stacked over or under.</summary>
    private sealed class FaceSet
    {
        private readonly List<WorldFace> _faces;
        private readonly float _splitHeight;
        private readonly Dictionary<(int, int), List<int>> _grid = new();

        public FaceSet(List<WorldFace> faces, float splitHeight)
        {
            _faces = faces;
            _splitHeight = splitHeight;
        }

        public void Add(int face)
        {
            foreach (var key in Cells(_faces[face], 0))
            {
                if (!_grid.TryGetValue(key, out var list))
                    _grid[key] = list = new List<int>();
                list.Add(face);
            }
        }

        public bool ConflictsWith(int face) => Conflicts(face).Any();

        /// <summary>Members that overlap the face in top-down view with more than the split height in between.</summary>
        public IEnumerable<int> Conflicts(int face)
        {
            var f = _faces[face];
            HashSet<int> seen = null;
            foreach (var key in Cells(f, 0))
            {
                if (!_grid.TryGetValue(key, out var list))
                    continue;
                foreach (int other in list)
                {
                    var o = _faces[other];
                    float gap = f.MaxY < o.MinY ? o.MinY - f.MaxY : o.MaxY < f.MinY ? f.MinY - o.MaxY : 0f;
                    if (gap <= _splitHeight)
                        continue;
                    if (!(seen ??= new HashSet<int>()).Add(other))
                        continue;
                    if (Geometry2D.ConvexOverlapXZ(f.Vertices, o.Vertices))
                        yield return other;
                }
            }
        }
    }
}
