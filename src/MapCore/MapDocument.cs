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

        [JsonIgnore]
        public Vector3 WorldPosition => new(World[0], World[1], World[2]);

        [JsonIgnore]
        public bool IsBonfire => Type == BonfireType;
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
