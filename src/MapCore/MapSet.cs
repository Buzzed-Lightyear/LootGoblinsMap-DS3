using System.Numerics;

namespace MapCore;

/// <summary>
/// The maps the builder produced, loaded on demand. The tracker asks it for the map the game
/// says the player is in; a map is read from disk the first time it is needed and kept.
/// </summary>
public sealed class MapSet
{
    private readonly string _root;
    private readonly Dictionary<string, MapData> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public MapIndex Index { get; }

    private MapSet(string root, MapIndex index)
    {
        _root = root;
        Index = index;
    }

    /// <summary>Ids of the maps that were built.</summary>
    public IEnumerable<string> Ids => Index.Maps.Where(m => m.Built).Select(m => m.Id);

    public bool Has(string mapId) => mapId != null && (Index.Find(mapId) != null || _loaded.ContainsKey(mapId));

    /// <summary>
    /// Opens the builder's output folder. With an index.json every map in it is available; an
    /// older output with only one map folder still works as a set of one.
    /// </summary>
    public static MapSet Load(string outRoot, string fallbackMapId)
    {
        string indexPath = Path.Combine(outRoot, MapIndex.FileName);
        if (File.Exists(indexPath))
        {
            var index = MapIndex.Load(indexPath);
            if (index.Maps.Any(m => m.Built))
                return new MapSet(outRoot, index);
        }

        string single = Path.Combine(outRoot, fallbackMapId ?? "");
        if (fallbackMapId != null && File.Exists(Path.Combine(single, MapDocument.FileName)))
            return Single(MapData.Load(single));

        throw new FileNotFoundException(
            $"No built maps found in '{outRoot}' (no {MapIndex.FileName}). Run the map builder first (see TESTING.md).", indexPath);
    }

    /// <summary>A set of one map that is already in memory (tests, single-map output).</summary>
    public static MapSet Single(MapData map)
    {
        var index = new MapIndex();
        index.Maps.Add(new MapIndex.Entry { Id = map.Id, Name = map.Name, Directory = map.Id, Built = true, ChecksPassed = true });
        var set = new MapSet(null, index);
        set._loaded[map.Id] = map;
        return set;
    }

    /// <summary>A set of maps that are already in memory (tests).</summary>
    public static MapSet Of(params MapData[] maps)
    {
        var index = new MapIndex();
        var set = new MapSet(null, index);
        foreach (var map in maps)
        {
            index.Maps.Add(new MapIndex.Entry { Id = map.Id, Name = map.Name, Directory = map.Id, Built = true, ChecksPassed = true });
            set._loaded[map.Id] = map;
        }
        index.MapsOverlapInSpace = true;
        return set;
    }

    /// <summary>The map with this id, or null when it was not built or cannot be loaded.</summary>
    public MapData Get(string mapId)
    {
        if (mapId == null)
            return null;
        if (_loaded.TryGetValue(mapId, out var map))
            return map;
        if (_failed.ContainsKey(mapId) || _root == null)
            return null;
        var entry = Index.Find(mapId);
        if (entry == null)
            return null;
        try
        {
            map = MapData.Load(Path.Combine(_root, entry.Directory ?? entry.Id));
            _loaded[mapId] = map;
            return map;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _failed[mapId] = ex.Message;
            return null;
        }
    }

    /// <summary>Why a map could not be loaded, if it failed.</summary>
    public string LoadProblem(string mapId) => mapId != null && _failed.TryGetValue(mapId, out var why) ? why : null;

    /// <summary>The name to show for a map id, built or not.</summary>
    public string NameOf(string mapId)
    {
        if (mapId == null)
            return null;
        var entry = Index.Maps.FirstOrDefault(m => string.Equals(m.Id, mapId, StringComparison.OrdinalIgnoreCase));
        return entry?.DisplayName ?? mapId;
    }

    /// <summary>
    /// The one map whose navmesh has this position within reach, or null when none or several do.
    /// Only a fallback for when the game's map id cannot be read: the builder's overlap analysis
    /// shows that some maps share space, so position alone is not always enough.
    /// </summary>
    public string LocateByPosition(Vector3 position)
    {
        string found = null;
        foreach (string id in Ids.ToList())
        {
            var map = Get(id);
            if (map == null)
                continue;
            if (position.X < map.MinX - 5 || position.X > map.MaxX + 5 || position.Z < map.MinZ - 5 || position.Z > map.MaxZ + 5)
                continue;
            var hit = map.Locator.Locate(position);
            if (!hit.Found || hit.Distance > PolygonLocator.DefaultReach)
                continue;
            if (found != null)
                return null;   // ambiguous
            found = id;
        }
        return found;
    }
}
