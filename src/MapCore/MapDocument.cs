using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapCore;

/// <summary>
/// The map.json model. The builder writes it and the tracker reads it through this one class,
/// so a field can never mean one thing to one program and something else to the other.
/// </summary>
public sealed class MapDocument
{
    public const string FileName = "map.json";

    public string Map { get; set; }

    /// <summary>The area's name from the game's own text (null when the game has none for this map).</summary>
    public string Name { get; set; }

    /// <summary>The archive the map came from; maps from the DLC archives are flagged.</summary>
    public string Source { get; set; }
    public bool Dlc { get; set; }

    /// <summary>World-space box of the main body of the navmesh: minX, maxX, minY, maxY, minZ, maxZ. The image covers its X/Z extent.</summary>
    public float[] Bounds { get; set; }

    /// <summary>Polygons that lie outside the image because they are stray pieces far from the main body.</summary>
    public int StrayPolygonCount { get; set; }
    public AxisConventionInfo AxisConvention { get; set; } = new();
    public TransformInfo Transform { get; set; } = new();

    /// <summary>File name of the navmesh polygon file next to map.json (see <see cref="NavPolyFile"/>).</summary>
    public string NavPolygons { get; set; }
    public int NavPolygonCount { get; set; }

    public List<LayerInfo> Layers { get; set; } = new();
    public List<MarkerInfo> Markers { get; set; } = new();

    public sealed class AxisConventionInfo
    {
        public string PixelX { get; set; }
        public string PixelY { get; set; }
        public string Up { get; set; }
        public string Note { get; set; }
        public bool FlipX { get; set; }
        public bool FlipZ { get; set; }
        /// <summary>Clockwise quarter turns applied after the flips.</summary>
        public int Rotation { get; set; }
    }

    public sealed class TransformInfo
    {
        public double OriginWorldX { get; set; }
        public double OriginWorldZ { get; set; }
        public double PixelsPerMeter { get; set; }
        /// <summary>Size of the rendered images (after rotation).</summary>
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        /// <summary>Image size before rotation; what the transform is defined on.</summary>
        public int BaseWidthPx { get; set; }
        public int BaseHeightPx { get; set; }
    }

    public sealed class LayerInfo
    {
        public int Index { get; set; }
        public float[] HeightRange { get; set; }
        public int FaceCount { get; set; }
        public string Svg { get; set; }
        public string Png { get; set; }
    }

    public sealed class MarkerInfo
    {
        public string Type { get; set; }
        public string Name { get; set; }
        public int EntityId { get; set; }
        public List<int> ItemLots { get; set; } = new();
        public float[] World { get; set; }
        public int Layer { get; set; }
        /// <summary>Pixel position in the builder's images (builder orientation).</summary>
        public double[] Pixel { get; set; }
        public double DistToLayerPolyM { get; set; }

        /// <summary>Bonfires: the name the game shows. Null when unresolved.</summary>
        public string DisplayName { get; set; }

        /// <summary>Treasures: what the marker holds, resolved from the item lots.</summary>
        public List<ItemInfo> Items { get; set; } = new();

        /// <summary>The category the marker is filtered and coloured by (see <see cref="ItemCategories"/>).</summary>
        public string Category { get; set; }

        /// <summary>
        /// The event flag that says this marker is done: picked up for a treasure, lit for a
        /// bonfire. 0 when the game data gives none.
        /// </summary>
        public int Flag { get; set; }

        /// <summary>True when something about the marker could not be resolved from the game data.</summary>
        public bool Unresolved { get; set; }

        /// <summary>
        /// A treasure event whose item lots do not exist in the game's params: a leftover that
        /// cannot be picked up. Kept for the record, never shown.
        /// </summary>
        public bool Unused { get; set; }

        /// <summary>The marker lies where there is no walkable polygon within reach; its layer is the nearest one by height.</summary>
        public bool OffMesh { get; set; }

        /// <summary>Set when the marker is defined in another map's file but stands on this map's floor.</summary>
        public string FromMap { get; set; }

        /// <summary>Why, when <see cref="Unresolved"/> is set.</summary>
        public List<string> Notes { get; set; } = new();

        /// <summary>Short label for the map: the bonfire name, or the item names.</summary>
        [JsonIgnore]
        public string Label
        {
            get
            {
                if (IsBonfire)
                    return DisplayName ?? "Bonfire";
                var named = Items.Where(i => i.Name != null).ToList();
                if (named.Count == 0)
                    return "Unknown item";
                return string.Join(", ", named.Select(i => i.Count > 1 ? $"{i.Name} ×{i.Count}" : i.Name));
            }
        }

        [JsonIgnore]
        public Vector3 WorldPosition => new(World[0], World[1], World[2]);

        [JsonIgnore]
        public bool IsBonfire => Type == BonfireType;
    }

    public sealed class ItemInfo
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public int Count { get; set; } = 1;
        public int ItemId { get; set; }
        /// <summary>100 for a certain item; lower when the lot draws one of several.</summary>
        public int ChancePercent { get; set; } = 100;
    }

    public const string BonfireType = "bonfire";
    public const string TreasureType = "treasure";

    /// <summary>Records a transform (and its orientation) in the document.</summary>
    public void SetTransform(MapTransform t)
    {
        Transform = new TransformInfo
        {
            OriginWorldX = t.OriginX,
            OriginWorldZ = t.OriginZ,
            PixelsPerMeter = t.Scale,
            WidthPx = t.Width,
            HeightPx = t.Height,
            BaseWidthPx = t.BaseWidth,
            BaseHeightPx = t.BaseHeight,
        };
        AxisConvention = new AxisConventionInfo
        {
            PixelX = t.FlipX ? "-worldX" : "+worldX",
            PixelY = t.FlipZ ? "-worldZ" : "+worldZ",
            Up = "worldY (not projected)",
            Note = "pixelX/pixelY describe the image before rotation; it is then turned clockwise by 'rotation' quarter turns. " +
                   "Check the orientation against the running game with the tracker (TESTING.md).",
            FlipX = t.FlipX,
            FlipZ = t.FlipZ,
            Rotation = t.Rotation,
        };
    }

    /// <summary>The transform exactly as the builder used it for its images and marker pixels.</summary>
    public MapTransform ToTransform()
    {
        // Files written before rotation existed have no base size; they were never rotated.
        int baseW = Transform.BaseWidthPx > 0 ? Transform.BaseWidthPx : Transform.WidthPx;
        int baseH = Transform.BaseHeightPx > 0 ? Transform.BaseHeightPx : Transform.HeightPx;
        return new MapTransform
        {
            OriginX = Transform.OriginWorldX,
            OriginZ = Transform.OriginWorldZ,
            Scale = Transform.PixelsPerMeter,
            BaseWidth = baseW,
            BaseHeight = baseH,
            FlipX = AxisConvention.FlipX,
            FlipZ = AxisConvention.FlipZ,
            Rotation = new Orientation(false, false, AxisConvention.Rotation).QuarterTurns,
        };
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));

    public static MapDocument Load(string path) =>
        JsonSerializer.Deserialize<MapDocument>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidDataException($"Empty map document: {path}");
}
