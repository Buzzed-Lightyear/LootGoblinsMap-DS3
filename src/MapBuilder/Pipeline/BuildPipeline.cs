using MapBuilder.GameData;
using MapBuilder.Render;
using MapCore;

namespace MapBuilder.Pipeline;

/// <summary>What the builder knows beyond a map's own files: the game's params and text, and where the map came from.</summary>
public sealed class MapBuildContext
{
    /// <summary>Null when the regulation or text could not be read: markers are then left unresolved.</summary>
    public ItemResolver Resolver;
    public string Source;
    public bool Dlc;
}

public sealed class BuildResult
{
    public string MapId;
    public string Name;
    public List<WorldFace> Faces;
    public List<Layer> Layers;
    public List<Marker> MarkerList;
    public MapTransform Transform;
    public string OutDir;
    /// <summary>World-space box of the main body (stray pieces excluded).</summary>
    public (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) Bounds;
    public int StrayPolygons;
    public int Seams;
    public int SelfOverlaps;
}

public static class BuildPipeline
{
    /// <summary>Padding around the navmesh bounds, in metres.</summary>
    public const double PadMeters = 3.0;

    public static BuildResult Run(Config cfg, string mapId, string extractDir, MapBuildContext context, TextWriter log)
    {
        var (faces, markers) = Decode(mapId, extractDir, log);
        return Export(cfg, mapId, faces, markers, context, log);
    }

    /// <summary>Reads a map's extracted files: the navmesh as world-space polygons and the markers from the map file.</summary>
    public static (List<WorldFace> Faces, List<Marker> Markers) Decode(string mapId, string extractDir, TextWriter log)
    {
        string nvaPath = Path.Combine(extractDir, $"{mapId}.nva");
        string bndPath = Path.Combine(extractDir, $"{mapId}.nvmhktbnd");
        string msbPath = Path.Combine(extractDir, $"{mapId}.msb");

        var faces = NavGeometry.Load(nvaPath, bndPath, log);
        log.WriteLine($"  Decoded {faces.Count} world-space polygons.");
        if (faces.Count == 0)
            throw new InvalidDataException("Navmesh decoded to zero polygons.");

        var markers = Markers.FromMsb(msbPath, log);
        return (faces, markers);
    }

    /// <summary>Export with the config's own map id and no game data: what the tests drive.</summary>
    public static BuildResult Export(Config cfg, List<WorldFace> faces, List<Marker> markers, TextWriter log) =>
        Export(cfg, cfg.MapId, faces, markers, null, log);

    /// <summary>
    /// Everything after decoding: layering, marker placement and naming, the transform, images,
    /// map.json and the polygon file. Takes plain geometry so tests can drive it without game files.
    /// </summary>
    public static BuildResult Export(Config cfg, string mapId, List<WorldFace> faces, List<Marker> markers,
                                     MapBuildContext context, TextWriter log)
    {
        List<Layer> layers;
        if (string.Equals(cfg.LayerMode, "stack", StringComparison.OrdinalIgnoreCase))
        {
            layers = LayerSplitter.Split(faces, cfg.LayerSplitHeight);
            // Order layers bottom-up by their minimum height and reindex.
            layers = layers.OrderBy(l => l.MinY).ToList();
            for (int i = 0; i < layers.Count; i++)
            {
                layers[i].Index = i;
                foreach (var f in layers[i].Faces)
                    f.Layer = i;
            }
            log.WriteLine("  Layering: stack order (every polygon on the lowest layer with room for it).");
        }
        else if (string.Equals(cfg.LayerMode, "floors", StringComparison.OrdinalIgnoreCase))
        {
            // Layers already come out bottom-up by mean height; keep that order.
            var split = FloorLayering.Split(faces, new FloorLayering.Options
            {
                SplitHeight = cfg.LayerSplitHeight, JoinStep = cfg.LayerJoinStep, FlatSlopeDegrees = cfg.LayerFlatSlopeDegrees,
            });
            layers = split.Layers;
            log.WriteLine($"  Layering: connected floors — {split.Links} links, {split.FlatPatches} level patches, {split.Regions} regions; " +
                          $"seams {split.SeamsBeforeRefining} → {split.Seams} after refining " +
                          $"(level/level {split.SeamsLevelToLevel}, level/steep {split.SeamsLevelToSteep}, steep/steep {split.SeamsSteepToSteep}).");
        }
        else
        {
            throw new InvalidDataException($"layerMode must be \"floors\" or \"stack\", not \"{cfg.LayerMode}\".");
        }
        int seams = FloorLayering.CountSeams(faces, cfg.LayerJoinStep);
        int selfOverlaps = FloorLayering.CountSelfOverlaps(faces, cfg.LayerSplitHeight);
        log.WriteLine($"  Split into {layers.Count} layers: " +
            string.Join(", ", layers.Select(l => $"#{l.Index}[{l.Faces.Count} faces, y {l.MinY:F1}..{l.MaxY:F1}]")) +
            $"; {seams} seams; {selfOverlaps} stacked pairs within a layer.");

        Markers.AssignLayers(markers, faces);
        Markers.Resolve(markers, context?.Resolver);

        // Image bounds: the main body only. Stray pieces stay in the data but do not stretch the image.
        int strays = MainBody.MarkStrays(faces);
        var body = faces.Where(f => !f.Stray).ToList();
        float minX = body.Min(f => f.MinX), maxX = body.Max(f => f.MaxX);
        float minZ = body.Min(f => f.MinZ), maxZ = body.Max(f => f.MaxZ);
        float minY = body.Min(f => f.MinY), maxY = body.Max(f => f.MaxY);
        var t = CreateTransform(cfg, minX, maxX, minZ, maxZ);
        log.WriteLine($"  Bounds: X {minX:F1}..{maxX:F1}  Y {minY:F1}..{maxY:F1}  Z {minZ:F1}..{maxZ:F1}  →  {t.Width}x{t.Height} px @ {t.Scale} px/m ({t.Orientation})" +
                      (strays > 0 ? $"; {strays} stray polygons left outside the image" : ""));

        foreach (var m in markers)
            (m.PixelX, m.PixelY) = t.ToPixel(m.World);

        string outDir = Path.Combine(cfg.ResolveOutDir(), mapId);
        Directory.CreateDirectory(outDir);

        // Remove layer images left by an earlier build that had more layers than this one.
        foreach (string stale in Directory.EnumerateFiles(outDir, "layer_*.*"))
        {
            string name = Path.GetFileName(stale);
            string ext = Path.GetExtension(name);
            if ((ext == ".png" || ext == ".svg") &&
                int.TryParse(name.AsSpan("layer_".Length, name.Length - "layer_".Length - ext.Length), out int index) &&
                index >= layers.Count)
                File.Delete(stale);
        }

        if (cfg.WriteLayerImages)
            foreach (var layer in layers)
                RenderLayer(outDir, layer, markers, t);

        // Navmesh polygons for the tracker: world-space vertices and layer index (stray pieces included).
        var polygons = faces.Select(f => new NavPolygon(f.Vertices, f.Layer)).ToList();
        NavPolyFile.Write(Path.Combine(outDir, NavPolyFile.FileName), polygons);

        string areaName = context?.Resolver?.AreaName(mapId);
        var bounds = (minX, maxX, minY, maxY, minZ, maxZ);
        WriteMapJson(mapId, areaName, context, cfg.WriteLayerImages, outDir, layers, markers, t, polygons.Count, bounds, strays);
        log.WriteLine($"  Wrote {(cfg.WriteLayerImages ? $"{layers.Count} layer SVG+PNG pairs, " : "")}{MapDocument.FileName} and {NavPolyFile.FileName} ({polygons.Count} polygons) to {outDir}");

        return new BuildResult
        {
            MapId = mapId, Name = areaName,
            Faces = faces, Layers = layers, MarkerList = markers, Transform = t, OutDir = outDir,
            Bounds = bounds, StrayPolygons = strays, Seams = seams, SelfOverlaps = selfOverlaps,
        };
    }

    /// <summary>The builder's world→pixel transform for a set of navmesh bounds.</summary>
    public static MapTransform CreateTransform(Config cfg, float minX, float maxX, float minZ, float maxZ) =>
        MapTransform.FitBounds(minX, maxX, minZ, maxZ, cfg.PixelsPerMeter, PadMeters, cfg.FlipX, cfg.FlipZ, cfg.Rotation);

    private static void RenderLayer(string outDir, Layer layer, List<Marker> markers, MapTransform t)
    {
        var polysPx = new List<(double, double)[]>(layer.Faces.Count);
        foreach (var f in layer.Faces.Where(f => !f.Stray))
            polysPx.Add(f.Vertices.Select(v => t.ToPixel(v)).ToArray());

        // Layer outline: edges used by only one polygon (shared helper, same as the tracker draws).
        var boundary = Geometry2D.OutlineEdgesXZ(layer.Faces.Where(f => !f.Stray).Select(f => f.Vertices))
            .Select(e => (t.ToPixel(e.A), t.ToPixel(e.B))).ToList();

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
            if (m.Type == MapDocument.BonfireType)
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

    private static void WriteMapJson(string mapId, string areaName, MapBuildContext context, bool images, string outDir,
                                     List<Layer> layers, List<Marker> markers, MapTransform t, int polygonCount,
                                     (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) b, int strays)
    {
        var doc = new MapDocument
        {
            Map = mapId,
            Name = areaName,
            Source = context?.Source,
            Dlc = context?.Dlc ?? false,
            Bounds = new[] { b.MinX, b.MaxX, b.MinY, b.MaxY, b.MinZ, b.MaxZ },
            StrayPolygonCount = strays,
            NavPolygons = NavPolyFile.FileName,
            NavPolygonCount = polygonCount,
            Layers = layers.Select(l => new MapDocument.LayerInfo
            {
                Index = l.Index,
                HeightRange = new[] { l.MinY, l.MaxY },
                FaceCount = l.Faces.Count,
                Svg = images ? $"layer_{l.Index}.svg" : null,
                Png = images ? $"layer_{l.Index}.png" : null,
            }).ToList(),
            Markers = markers.Select(m => new MapDocument.MarkerInfo
            {
                Type = m.Type,
                Name = m.Name,
                EntityId = m.EntityId,
                ItemLots = m.ItemLots,
                World = new[] { m.World.X, m.World.Y, m.World.Z },
                Layer = m.Layer,
                Pixel = new[] { m.PixelX, m.PixelY },
                DistToLayerPolyM = m.DistToLayerPolyM == float.MaxValue ? -1 : Math.Round(m.DistToLayerPolyM, 3),
                DisplayName = m.DisplayName,
                Items = m.Items,
                Category = m.Category,
                Flag = m.Flag,
                Unresolved = m.Unresolved,
                Unused = m.Unused,
                FromMap = m.FromMap,
                Notes = m.Notes,
            }).ToList(),
        };
        doc.SetTransform(t);
        doc.Save(Path.Combine(outDir, MapDocument.FileName));
    }
}
