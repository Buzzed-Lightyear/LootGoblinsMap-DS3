using System.Numerics;
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
                    Type = "bonfire",
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
                Type = "treasure",
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
        }
    }
}
