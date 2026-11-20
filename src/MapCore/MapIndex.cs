using System.Text.Json;

namespace MapCore;

/// <summary>
/// <c>out/index.json</c>: every map the builder produced, with its name and bounds. The tracker
/// reads it to know which maps exist and loads a map's folder when the player enters it.
/// </summary>
public sealed class MapIndex
{
    public const string FileName = "index.json";

    public string Language { get; set; }
    public string BuiltUtc { get; set; }

    /// <summary>How the tracker should tell which map the player is in: "mapId" (read from memory) is always tried first.</summary>
    public bool MapsOverlapInSpace { get; set; }

    public List<Entry> Maps { get; set; } = new();

    public sealed class Entry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>Folder of the map's files, relative to the index.</summary>
        public string Directory { get; set; }
        public string Source { get; set; }
        public bool Dlc { get; set; }
        /// <summary>World-space box of the main body: minX, maxX, minY, maxY, minZ, maxZ.</summary>
        public float[] Bounds { get; set; }
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public int Layers { get; set; }
        public int Polygons { get; set; }
        public int Bonfires { get; set; }
        public int Treasures { get; set; }
        /// <summary>False when the map failed to build; <see cref="Problem"/> says why and there is no folder to load.</summary>
        public bool Built { get; set; }
        public bool ChecksPassed { get; set; }
        public string Problem { get; set; }

        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    public Entry Find(string mapId) => Maps.FirstOrDefault(m => m.Built && string.Equals(m.Id, mapId, StringComparison.OrdinalIgnoreCase));

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, MapDocument.JsonOpts));

    public static MapIndex Load(string path) =>
        JsonSerializer.Deserialize<MapIndex>(File.ReadAllText(path), MapDocument.JsonOpts)
        ?? throw new InvalidDataException($"Empty map index: {path}");
}
