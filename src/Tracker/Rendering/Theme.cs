using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Tracker.Rendering;

/// <summary>
/// Everything about how the map looks, in one place: colours, line weights, fonts, icon sizes
/// and opacities. Two themes are built in: "parchment" (ink on aged paper, the default) and
/// "plain" (the flat look of the earlier versions). Reference copies of both are written to
/// <c>themesuiltin</c> in the data folder on every start. A JSON file directly in
/// <c>themes</c> is a theme of the user's own; one named like a built-in theme replaces it.
/// </summary>
public sealed class Theme
{
    public string Name { get; set; } = "plain";

    // ---- surfaces ----
    /// <summary>Background of a solid panel (debug window, exports).</summary>
    public string Background { get; set; } = "#1B1F24";
    /// <summary>Background of the overlay's translucent panels.</summary>
    public string Panel { get; set; } = "#E010141A";
    /// <summary>Background of the full map and of exported pictures: the paper. Empty uses <see cref="Panel"/> or <see cref="Background"/>.</summary>
    public string Paper { get; set; } = "";
    public string PanelBorder { get; set; } = "#A0E6EDF3";
    public double PanelBorderWidth { get; set; } = 1.2;
    /// <summary>Draw the minimap as a disc instead of a rounded square.</summary>
    public bool RoundMinimap { get; set; }

    // ---- floors ----
    /// <summary>Fill of the layer being shown.</summary>
    public string Floor { get; set; } = "#8D98A6";
    /// <summary>Fill of the other layers; lower layers fade further by <see cref="LowerLayerFade"/>.</summary>
    public string FloorOther { get; set; } = "#428D98A6";
    /// <summary>Fill of the layers above the shown one; empty uses <see cref="FloorOther"/>.</summary>
    public string FloorAbove { get; set; } = "";
    /// <summary>Fill of every layer while no layer is known.</summary>
    public string FloorNeutral { get; set; } = "#998D98A6";
    public string Outline { get; set; } = "#E6EDF3";
    public string OutlineOther { get; set; } = "#508D98A6";
    /// <summary>Width, in screen pixels, of the outline of the shown layer and of the other layers.</summary>
    public double OutlineWidth { get; set; } = 1.4;
    public double OutlineOtherWidth { get; set; } = 1.0;
    /// <summary>Extra opacity factor (0..1) applied per layer below the shown one; 1 means no extra fade.</summary>
    public double LowerLayerFade { get; set; } = 1.0;
    /// <summary>Opacity of the shadow layers above the shown one cast onto it; 0 turns it off.</summary>
    public double UpperLayerShadow { get; set; }
    /// <summary>Offset of that shadow in metres (towards bottom right).</summary>
    public double ShadowOffsetMeters { get; set; } = 0.6;
    public string Shadow { get; set; } = "#000000";

    // ---- stairs and ramps ----
    /// <summary>Hatch lines across polygons steeper than <see cref="SteepDegrees"/>; empty turns them off.</summary>
    public string Hatch { get; set; } = "";
    public double HatchWidth { get; set; } = 1.0;
    /// <summary>Distance between hatch lines in metres.</summary>
    public double HatchSpacingMeters { get; set; } = 0.9;
    public double SteepDegrees { get; set; } = 15;

    // ---- paper ----
    /// <summary>Strength (0..1) of the procedural paper grain on the full map and exports; 0 turns it off.</summary>
    public double Grain { get; set; }
    /// <summary>Strength (0..1) of the darkening towards the edges of the full map; 0 turns it off.</summary>
    public double Vignette { get; set; }

    // ---- markers ----
    public string Bonfire { get; set; } = "#FF8C1A";
    public string BonfireCore { get; set; } = "#4D2600";
    public string MarkerOutline { get; set; } = "#D010141A";
    public double MarkerOutlineWidth { get; set; } = 1.2;
    /// <summary>Half the size of an item icon, and the radius of a bonfire, in screen pixels.</summary>
    public double ItemIconSize { get; set; } = 6;
    public double BonfireIconSize { get; set; } = 9;
    /// <summary>Draw each category as its own icon; false draws every item as a diamond.</summary>
    public bool CategoryIcons { get; set; }
    /// <summary>A dark disc under each item icon, so the icon reads on paper and over the game alike; empty draws none.</summary>
    public string IconPlate { get; set; } = "";
    /// <summary>Opacity of markers on a layer other than the shown one, and of collected markers when shown.</summary>
    public double OtherLayerMarkerOpacity { get; set; } = 0.45;
    public double CollectedMarkerOpacity { get; set; } = 0.35;
    public Dictionary<string, string> CategoryColors { get; set; } = new()
    {
        ["key"] = "#FFD60A", ["ring"] = "#C77DFF", ["weapon"] = "#FF70A6", ["shield"] = "#FF9E6D", ["catalyst"] = "#7AE0FF",
        ["armor"] = "#06D6A0", ["spell"] = "#5AA9FF", ["upgrade"] = "#B5E48C", ["soul"] = "#F8F9FA", ["ammo"] = "#D8C99B",
        ["consumable"] = "#ADB5BD", ["unknown"] = "#8A6F5A",
    };

    // ---- player and trail ----
    public string Player { get; set; } = "#FF3B30";
    public string PlayerRing { get; set; } = "#FFFFFF";
    public double PlayerSize { get; set; } = 5;
    public string Trail { get; set; } = "#3DD6F5";
    public double TrailWidth { get; set; } = 2.5;

    // ---- lettering ----
    /// <summary>"bundled" for the bundled serif, or the name of an installed font.</summary>
    public string LabelFont { get; set; } = "Segoe UI";
    public string TitleFont { get; set; } = "Segoe UI";
    public double LabelSize { get; set; } = 12.5;
    public double TitleSize { get; set; } = 17;
    public double LegendSize { get; set; } = 13;
    public string Text { get; set; } = "#F2F5F8";
    public string TextMuted { get; set; } = "#B6C0CC";
    public string LabelBackground { get; set; } = "#D80D1117";
    public string Banner { get; set; } = "#E65C1F1F";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>The flat look of the earlier versions.</summary>
    public static Theme Plain() => new() { Name = "plain" };

    /// <summary>Ink on aged paper: muted washes, dark outlines, serif lettering, category icons.</summary>
    public static Theme Parchment() => new()
    {
        Name = "parchment",
        Background = "#2A241B",
        Panel = "#F0221C14",
        Paper = "#CBB789",
        PanelBorder = "#C08C7A55",
        PanelBorderWidth = 1.5,
        RoundMinimap = true,
        Floor = "#F0E5C6",
        FloorOther = "#8CE2D4AC",
        FloorAbove = "#C09A8459",
        FloorNeutral = "#C8E6DAB6",
        Outline = "#2B2118",
        OutlineOther = "#A85A4A33",
        OutlineWidth = 1.8,
        OutlineOtherWidth = 0.9,
        LowerLayerFade = 0.8,
        UpperLayerShadow = 0.28,
        ShadowOffsetMeters = 0.7,
        Shadow = "#120C06",
        Hatch = "#8A5A4632",
        HatchWidth = 0.9,
        HatchSpacingMeters = 0.8,
        Grain = 0.4,
        Vignette = 0.5,
        Bonfire = "#E0792B",
        BonfireCore = "#FFE7A8",
        MarkerOutline = "#F01A130C",
        MarkerOutlineWidth = 1.1,
        ItemIconSize = 9,
        BonfireIconSize = 11,
        CategoryIcons = true,
        IconPlate = "#F2231B12",
        CategoryColors = new()
        {
            ["key"] = "#F0C94A", ["ring"] = "#C79AE8", ["weapon"] = "#E8694E", ["shield"] = "#E09A52", ["catalyst"] = "#72C9DE",
            ["armor"] = "#74C29B", ["spell"] = "#7FA2EE", ["upgrade"] = "#B7D17C", ["soul"] = "#F4EEDC", ["ammo"] = "#D2BE94",
            ["consumable"] = "#B9B2A4", ["unknown"] = "#9A8068",
        },
        Player = "#B3261E",
        PlayerRing = "#FFF4DC",
        PlayerSize = 5.5,
        Trail = "#7A2E1F",
        TrailWidth = 2.0,
        LabelFont = "bundled",
        TitleFont = "bundled",
        LabelSize = 14.5,
        TitleSize = 21,
        LegendSize = 14.5,
        Text = "#F3E9D2",
        TextMuted = "#C9B894",
        LabelBackground = "#D01C160F",
        Banner = "#E0401A12",
    };

    public static IReadOnlyList<Theme> BuiltIn() => new[] { Parchment(), Plain() };

    /// <summary>Names of the themes that can be chosen: the built-in ones plus any JSON file in the themes folder.</summary>
    public static List<string> Available(string? themesDir)
    {
        var names = BuiltIn().Select(t => t.Name).ToList();
        try
        {
            if (themesDir != null && Directory.Exists(themesDir))
                foreach (string file in Directory.EnumerateFiles(themesDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>Folder, inside the themes folder, that holds the reference copies of the built-in themes.</summary>
    public const string BuiltInFolder = "builtin";

    /// <summary>
    /// Loads a theme by name. A file in the themes folder wins over a built-in theme of the same
    /// name. The built-in definitions are kept as reference copies in <see cref="BuiltInFolder"/>,
    /// rewritten whenever they differ, so a new version's look arrives without touching the
    /// user's own files. An unreadable file falls back to the built-in theme of that name, or to "plain".
    /// </summary>
    public static Theme Load(string? themesDir, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "parchment" : name;
        var builtIn = BuiltIn().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (themesDir == null)
            return builtIn ?? Plain();
        try
        {
            string reference = Path.Combine(themesDir, BuiltInFolder);
            Directory.CreateDirectory(reference);
            foreach (var theme in BuiltIn())
            {
                string builtInPath = Path.Combine(reference, theme.Name + ".json");
                string json = theme.ToJson();
                if (!File.Exists(builtInPath) || File.ReadAllText(builtInPath) != json)
                    File.WriteAllText(builtInPath, json);
            }
            string path = Path.Combine(themesDir, name + ".json");
            if (File.Exists(path) && JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), JsonOpts) is { } loaded)
            {
                loaded.Name = name;
                // A file written by an older version may lack colours for newer categories.
                foreach (var (category, colour) in (builtIn ?? Plain()).CategoryColors)
                    loaded.CategoryColors.TryAdd(category, colour);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return builtIn ?? Plain();
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static Theme FromJson(string json) => JsonSerializer.Deserialize<Theme>(json, JsonOpts) ?? Plain();

    /// <summary>"#RRGGBB" or "#AARRGGBB" to a colour; anything unreadable becomes magenta so it is noticed.</summary>
    public static Color ParseColor(string? text)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color c)
                return c;
        }
        catch (FormatException) { }
        return Colors.Magenta;
    }
}
