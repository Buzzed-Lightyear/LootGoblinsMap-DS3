using System.Numerics;
using MapBuilder.GameData;
using MapCore;
using SoulsFormats;

namespace MapBuilder.Pipeline;

public sealed class Marker
{
    public string Type;          // "bonfire" | "treasure"
    public string Name;          // MSB part/event name
    public int EntityId;
    public List<int> ItemLots = new();
    public Vector3 World;
    public int Layer = -1;
    public float DistToLayerPolyM = float.MaxValue;
    public double PixelX, PixelY;

    // Filled from the game's params and text (ItemResolver); see MapDocument.MarkerInfo.
    public string DisplayName;
    public List<MapDocument.ItemInfo> Items = new();
    public string Category;
    public int Flag;
    public bool Unresolved;
    public List<string> Notes = new();
    public bool Unused;
    /// <summary>Placed on the nearest layer by height because no navmesh polygon is within reach of it.</summary>
    public bool OffMesh;
    public string FromMap;
    public bool Resolved;
}

public static class Markers
{
    /// <summary>DS3 bonfire object model.</summary>
    public const string BonfireModel = "o000100";

    public static List<Marker> FromMsb(string msbPath, TextWriter log)
    {
        var msb = MSB3.Read(msbPath);
        var markers = new List<Marker>();

        // Name -> part, across all part types (treasure parts are usually objects).
        var partsByName = new Dictionary<string, MSB3.Part>();
        foreach (var part in msb.Parts.GetEntries())
            partsByName.TryAdd(part.Name, part);

        foreach (var obj in msb.Parts.Objects)
        {
            if (string.Equals(obj.ModelName, BonfireModel, StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(new Marker
                {
                    Type = MapDocument.BonfireType,
                    Name = obj.Name,
                    EntityId = obj.EntityID,
                    World = obj.Position,
                });
            }
        }

        foreach (var t in msb.Events.Treasures)
        {
            // The holding part is usually TreasurePartName; some entries use the event's base PartName instead.
            string partName = !string.IsNullOrEmpty(t.TreasurePartName) ? t.TreasurePartName : t.PartName;
            if (string.IsNullOrEmpty(partName) || !partsByName.TryGetValue(partName, out var part))
            {
                log.WriteLine($"  WARNING: treasure '{t.Name}' (itemlots {t.ItemLot1}/{t.ItemLot2}) has no resolvable part ('{partName}') — skipped.");
                continue;
            }
            var m = new Marker
            {
                Type = MapDocument.TreasureType,
                Name = $"{t.Name} ({t.TreasurePartName})",
                EntityId = t.EventID,
                World = part.Position,
            };
            if (t.ItemLot1 != -1) m.ItemLots.Add(t.ItemLot1);
            if (t.ItemLot2 != -1) m.ItemLots.Add(t.ItemLot2);
            markers.Add(m);
        }

        log.WriteLine($"MSB: {markers.Count(m => m.Type == "bonfire")} bonfires, {markers.Count(m => m.Type == "treasure")} treasures.");
        return markers;
    }

    /// <summary>
    /// Assigns each marker to the layer of the best nearby face: smallest combination of
    /// horizontal distance to the polygon and vertical offset from the face's height span.
    /// Records the horizontal distance for the acceptance check.
    /// </summary>
    public static void AssignLayers(List<Marker> markers, List<WorldFace> faces)
    {
        foreach (var m in markers)
        {
            WorldFace best = null;
            float bestScore = float.MaxValue;
            float bestHoriz = float.MaxValue;
            foreach (var f in faces)
            {
                // Cheap reject on expanded bounding box.
                if (m.World.X < f.MinX - 8 || m.World.X > f.MaxX + 8 ||
                    m.World.Z < f.MinZ - 8 || m.World.Z > f.MaxZ + 8)
                    continue;
                float vert = m.World.Y < f.MinY ? f.MinY - m.World.Y
                           : m.World.Y > f.MaxY ? m.World.Y - f.MaxY : 0f;
                if (vert > 6f)
                    continue;
                float horiz = Geometry2D.DistancePointToPolyXZ(m.World, f.Vertices);
                float score = horiz + 0.5f * vert;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = f;
                    bestHoriz = horiz;
                }
            }
            if (best != null)
            {
                m.Layer = best.Layer;
                m.DistToLayerPolyM = bestHoriz;
            }
            else if (!m.Unused && m.Type != MapDocument.BonfireType)
            {
                PlaceOffMesh(m, faces);
            }
        }
    }

    /// <summary>
    /// Fills in what the game's params and text say about each marker: a bonfire's name and
    /// lit flag, a treasure's items, category and pickup flag. Without a resolver (the game data
    /// could not be read) every marker is left marked as unresolved.
    /// </summary>
    public static void Resolve(List<Marker> markers, ItemResolver resolver)
    {
        foreach (var m in markers)
        {
            if (m.Resolved)
                continue;
            m.Resolved = true;
            if (m.Type == MapDocument.BonfireType)
            {
                m.Category = MapDocument.BonfireType;
                if (resolver == null)
                {
                    m.Unresolved = true;
                    m.Notes.Add("game data not loaded");
                }
                else if (resolver.TryGetBonfire(m.EntityId, out var info))
                {
                    m.DisplayName = info.Name;
                    m.Flag = info.LitFlag;
                    if (info.Name == null)
                    {
                        m.Unresolved = true;
                        m.Notes.Add("the bonfire's name id has no text");
                    }
                }
                else
                {
                    m.Unresolved = true;
                    m.Notes.Add($"entity {m.EntityId} is not in the game's bonfire table");
                }
                continue;
            }

            if (resolver == null)
            {
                m.Category = ItemCategories.Unknown;
                m.Unresolved = true;
                m.Notes.Add("game data not loaded");
                continue;
            }
            if (m.ItemLots.Count > 0 && m.ItemLots.All(lot => !resolver.LotExists(lot)))
            {
                // Nothing to pick up: the event points at lots the game does not have.
                m.Category = ItemCategories.Unknown;
                m.Unused = true;
                m.Unresolved = true;
                m.Notes.Add($"unused treasure event: item lot {string.Join("/", m.ItemLots)} is not in the regulation");
                continue;
            }
            var treasure = resolver.Resolve(m.ItemLots);
            m.Items = treasure.Items;
            m.Flag = treasure.Flag;
            m.Unresolved = treasure.Unresolved;
            m.Notes.AddRange(treasure.Notes);
            if (treasure.Flag <= 0)
                m.Notes.Add("the lot has no pickup flag; the marker cannot be hidden when collected");
            m.Category = ItemCategories.Primary(treasure.Items.Select(i => i.Category));
        }
    }

    /// <summary>
    /// Top-down distance from a point to the nearest polygon that is within 6 m of its height
    /// (the same window the layer assignment uses); float.MaxValue when there is none nearby.
    /// </summary>
    public static float DistanceToMesh(Vector3 p, List<WorldFace> faces)
    {
        float best = float.MaxValue;
        foreach (var f in faces)
        {
            if (p.X < f.MinX - 8 || p.X > f.MaxX + 8 || p.Z < f.MinZ - 8 || p.Z > f.MaxZ + 8)
                continue;
            float vert = p.Y < f.MinY ? f.MinY - p.Y : p.Y > f.MaxY ? p.Y - f.MaxY : 0f;
            if (vert > 6f)
                continue;
            best = Math.Min(best, Geometry2D.DistancePointToPolyXZ(p, f.Vertices));
        }
        return best;
    }

    /// <summary>
    /// A real treasure with no walkable polygon within reach (it lies on a ledge, a roof or a
    /// spot the navigation data does not cover) still belongs on the map. It is put on the layer
    /// of the polygon nearest to it in height, looking outwards from 40 m until something is found.
    /// </summary>
    private static void PlaceOffMesh(Marker m, List<WorldFace> faces)
    {
        for (float radius = 40f; radius <= 2560f; radius *= 2)
        {
            WorldFace nearest = null;
            float bestVertical = float.MaxValue, bestHorizontal = float.MaxValue;
            foreach (var f in faces)
            {
                if (m.World.X < f.MinX - radius || m.World.X > f.MaxX + radius ||
                    m.World.Z < f.MinZ - radius || m.World.Z > f.MaxZ + radius)
                    continue;
                float horizontal = Geometry2D.DistancePointToPolyXZ(m.World, f.Vertices);
                if (horizontal > radius)
                    continue;
                float vertical = m.World.Y < f.MinY ? f.MinY - m.World.Y : m.World.Y > f.MaxY ? m.World.Y - f.MaxY : 0f;
                if (vertical < bestVertical - 0.01f || (MathF.Abs(vertical - bestVertical) <= 0.01f && horizontal < bestHorizontal))
                {
                    bestVertical = vertical;
                    bestHorizontal = horizontal;
                    nearest = f;
                }
            }
            if (nearest == null)
                continue;
            m.Layer = nearest.Layer;
            m.DistToLayerPolyM = bestHorizontal;
            m.OffMesh = true;
            m.Notes.Add($"no walkable polygon within reach; shown on the layer nearest in height ({bestVertical:F1} m above or below, {bestHorizontal:F1} m to the side)");
            return;
        }
    }
}
