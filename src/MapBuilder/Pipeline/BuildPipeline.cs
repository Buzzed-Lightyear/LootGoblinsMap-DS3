using System.Numerics;
using System.Text.Json;
using MapBuilder.Render;

namespace MapBuilder.Pipeline;

public sealed class BuildResult
{
    public List<WorldFace> Faces;
    public List<Layer> Layers;
    public List<Marker> MarkerList;
    public MapTransform Transform;
    public string OutDir;
    public (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) Bounds;
}

public static class BuildPipeline
{
    public static BuildResult Run(Config cfg, string extractDir, TextWriter log)
    {
        string nvaPath = Path.Combine(extractDir, $"{cfg.MapId}.nva");
        string bndPath = Path.Combine(extractDir, $"{cfg.MapId}.nvmhktbnd");
        string msbPath = Path.Combine(extractDir, $"{cfg.MapId}.msb");

        var faces = NavGeometry.Load(nvaPath, bndPath, log);
        log.WriteLine($"Decoded {faces.Count} world-space polygons.");
        if (faces.Count == 0)
            throw new InvalidDataException("Navmesh decoded to zero polygons.");

        var layers = LayerSplitter.Split(faces, cfg.LayerSplitHeight);
        // Order layers bottom-up by their minimum height and reindex.
        layers = layers.OrderBy(l => l.MinY).ToList();
        for (int i = 0; i < layers.Count; i++)
        {
            layers[i].Index = i;
            foreach (var f in layers[i].Faces)
                f.Layer = i;
        }
        log.WriteLine($"Split into {layers.Count} layers: " +
            string.Join(", ", layers.Select(l => $"#{l.Index}[{l.Faces.Count} faces, y {l.MinY:F1}..{l.MaxY:F1}]")));

        var markers = Markers.FromMsb(msbPath, log);
        Markers.AssignLayers(markers, faces);

        float minX = faces.Min(f => f.MinX), maxX = faces.Max(f => f.MaxX);
        float minZ = faces.Min(f => f.MinZ), maxZ = faces.Max(f => f.MaxZ);
        float minY = faces.Min(f => f.MinY), maxY = faces.Max(f => f.MaxY);
        var t = MapTransform.FitBounds(minX, maxX, minZ, maxZ, cfg.PixelsPerMeter, 3.0, cfg.FlipX, cfg.FlipZ);
        log.WriteLine($"Bounds: X {minX:F1}..{maxX:F1}  Y {minY:F1}..{maxY:F1}  Z {minZ:F1}..{maxZ:F1}  →  {t.Width}x{t.Height} px @ {t.Scale} px/m");

        foreach (var m in markers)
            (m.PixelX, m.PixelY) = t.ToPixel(m.World);

        string outDir = Path.Combine(cfg.ResolveOutDir(), cfg.MapId);
        Directory.CreateDirectory(outDir);

        foreach (var layer in layers)
            RenderLayer(outDir, layer, markers, t);

        WriteMapJson(cfg, outDir, layers, markers, t);
        log.WriteLine($"Wrote {layers.Count} layer SVG+PNG pairs and map.json to {outDir}");

        return new BuildResult
        {
            Faces = faces, Layers = layers, MarkerList = markers, Transform = t, OutDir = outDir,
            Bounds = (minX, maxX, minY, maxY, minZ, maxZ),
        };
    }

    private static void RenderLayer(string outDir, Layer layer, List<Marker> markers, MapTransform t)
    {
        var polysPx = new List<(double, double)[]>(layer.Faces.Count);
        foreach (var f in layer.Faces)
            polysPx.Add(f.Vertices.Select(v => t.ToPixel(v)).ToArray());

        var boundary = BoundaryEdges(layer, t);

        // SVG
        SvgWriter.WriteLayer(Path.Combine(outDir, $"layer_{layer.Index}.svg"), layer, polysPx, boundary, markers, t);

        // PNG
        var img = new Rgba32Image(t.Width, t.Height);
        img.Fill(27, 31, 36, 255);
        foreach (var poly in polysPx)
            img.FillPolygon(poly, 141, 152, 166, 255);
        foreach (var (a, b) in boundary)
            img.DrawLine(a.Item1, a.Item2, b.Item1, b.Item2, 45, 51, 59, 255);
        foreach (var m in markers.Where(m => m.Layer == layer.Index))
        {
            if (m.Type == "bonfire")
            {
                img.FillCircle(m.PixelX, m.PixelY, 6, 255, 140, 26, 255);
                img.FillCircle(m.PixelX, m.PixelY, 2.5, 77, 38, 0, 255);
            }
            else
            {
                img.FillCircle(m.PixelX, m.PixelY, 4, 255, 214, 51, 255);
            }
        }
        PngWriter.Write(Path.Combine(outDir, $"layer_{layer.Index}.png"), img);
    }

    /// <summary>Edges that belong to exactly one polygon of the layer (quantized to 1 cm).</summary>
    private static List<((double, double) A, (double, double) B)> BoundaryEdges(Layer layer, MapTransform t)
    {
        var counts = new Dictionary<(long, long, long, long), (Vector3 A, Vector3 B, int N)>();
        foreach (var f in layer.Faces)
        {
            for (int i = 0; i < f.Vertices.Length; i++)
            {
                var a = f.Vertices[i];
                var b = f.Vertices[(i + 1) % f.Vertices.Length];
                var ka = ((long)MathF.Round(a.X * 100), (long)MathF.Round(a.Z * 100));
                var kb = ((long)MathF.Round(b.X * 100), (long)MathF.Round(b.Z * 100));
                var key = ka.CompareTo(kb) <= 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2)
                                                : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
                counts[key] = counts.TryGetValue(key, out var e) ? (e.A, e.B, e.N + 1) : (a, b, 1);
            }
        }
        var result = new List<((double, double), (double, double))>();
        foreach (var (_, e) in counts)
            if (e.N == 1)
                result.Add((t.ToPixel(e.A), t.ToPixel(e.B)));
        return result;
    }

    private static void WriteMapJson(Config cfg, string outDir, List<Layer> layers, List<Marker> markers, MapTransform t)
    {
        var doc = new
        {
            map = cfg.MapId,
            axisConvention = new
            {
                pixelX = cfg.FlipX ? "-worldX" : "+worldX",
                pixelY = cfg.FlipZ ? "-worldZ" : "+worldZ",
                up = "worldY (not projected)",
                note = "Orientation vs. the in-game compass is unverified; FlipX/FlipZ in the config mirror the map in one switch.",
                flipX = cfg.FlipX,
                flipZ = cfg.FlipZ,
            },
            transform = new
            {
                originWorldX = t.OriginX,
                originWorldZ = t.OriginZ,
                pixelsPerMeter = t.Scale,
                widthPx = t.Width,
                heightPx = t.Height,
            },
            layers = layers.Select(l => new
            {
                index = l.Index,
                heightRange = new[] { l.MinY, l.MaxY },
                faceCount = l.Faces.Count,
                svg = $"layer_{l.Index}.svg",
                png = $"layer_{l.Index}.png",
            }),
            markers = markers.Select(m => new
            {
                type = m.Type,
                name = m.Name,
                entityId = m.EntityId,
                itemLots = m.ItemLots,
                world = new[] { m.World.X, m.World.Y, m.World.Z },
                layer = m.Layer,
                pixel = new[] { m.PixelX, m.PixelY },
                distToLayerPolyM = m.DistToLayerPolyM == float.MaxValue ? -1 : Math.Round(m.DistToLayerPolyM, 3),
            }),
        };
        File.WriteAllText(Path.Combine(outDir, "map.json"), JsonSerializer.Serialize(doc, Config.JsonOpts));
    }
}
