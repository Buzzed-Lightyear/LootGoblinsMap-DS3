namespace MapCore;

/// <summary>
/// Everything the tracker needs from a builder output folder: the map.json document, the
/// navmesh polygons and the lookup over them. Loaded through the same classes the builder
/// writes with.
/// </summary>
public sealed class MapData
{
    public string Directory { get; private init; }

    /// <summary>The map id, for example "m30_00_00_00".</summary>
    public string Id => Document.Map;

    /// <summary>The area's name from the game's text, or the map id when the game has none.</summary>
    public string Name => string.IsNullOrEmpty(Document.Name) ? Document.Map : Document.Name;
    public MapDocument Document { get; private init; }
    public IReadOnlyList<NavPolygon> Polygons { get; private init; }
    public PolygonLocator Locator { get; private init; }
    public int LayerCount { get; private init; }

    /// <summary>World-space bounds of the navmesh (the map's extent).</summary>
    public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

    public IReadOnlyList<MapDocument.MarkerInfo> Markers => Document.Markers;

    /// <summary>The builder's transform, turned the way the caller wants it.</summary>
    public MapTransform TransformFor(Orientation orientation) =>
        Document.ToTransform().WithOrientation(orientation);

    public static MapData Load(string mapDirectory)
    {
        string jsonPath = Path.Combine(mapDirectory, MapDocument.FileName);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException($"'{jsonPath}' not found. Run the map builder first (see TESTING.md).", jsonPath);
        var doc = MapDocument.Load(jsonPath);

        string polyPath = Path.Combine(mapDirectory, string.IsNullOrEmpty(doc.NavPolygons) ? NavPolyFile.FileName : doc.NavPolygons);
        if (!File.Exists(polyPath))
            throw new FileNotFoundException($"'{polyPath}' not found. Re-run the map builder; older outputs have no polygon file.", polyPath);
        var polygons = NavPolyFile.Read(polyPath);
        if (polygons.Count == 0)
            throw new InvalidDataException($"'{polyPath}' contains no polygons.");

        return Create(mapDirectory, doc, polygons);
    }

    /// <summary>Builds the same object from data already in memory (tests, tools).</summary>
    public static MapData Create(string mapDirectory, MapDocument doc, IReadOnlyList<NavPolygon> polygons) => new()
    {
        Directory = mapDirectory,
        Document = doc,
        Polygons = polygons,
        Locator = new PolygonLocator(polygons),
        LayerCount = Math.Max(doc.Layers.Count, polygons.Max(p => p.Layer) + 1),
        MinX = polygons.Min(p => p.MinX), MaxX = polygons.Max(p => p.MaxX),
        MinY = polygons.Min(p => p.MinY), MaxY = polygons.Max(p => p.MaxY),
        MinZ = polygons.Min(p => p.MinZ), MaxZ = polygons.Max(p => p.MaxZ),
    };
}
