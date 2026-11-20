using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapCore;

/// <summary>A global keyboard shortcut: modifiers plus one key, written like "Ctrl+Shift+F7".</summary>
public readonly record struct Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key, int VirtualKey)
{
    // RegisterHotKey modifier bits.
    public const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    public uint Modifiers => (Alt ? ModAlt : 0) | (Ctrl ? ModCtrl : 0) | (Shift ? ModShift : 0) | (Win ? ModWin : 0);

    public override string ToString() =>
        (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + (Win ? "Win+" : "") + Key;

    /// <summary>Virtual-key codes of the keys a shortcut may end in, by the name used in the settings file.</summary>
    private static readonly Dictionary<string, int> Keys = BuildKeys();

    private static Dictionary<string, int> BuildKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++)
            keys[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++)
            keys[c.ToString()] = c;
        for (int f = 1; f <= 24; f++)
            keys["F" + f] = 0x70 + f - 1;
        for (int n = 0; n <= 9; n++)
            keys["Num" + n] = 0x60 + n;
        keys["PageUp"] = 0x21; keys["PageDown"] = 0x22; keys["End"] = 0x23; keys["Home"] = 0x24;
        keys["Left"] = 0x25; keys["Up"] = 0x26; keys["Right"] = 0x27; keys["Down"] = 0x28;
        keys["Insert"] = 0x2D; keys["Delete"] = 0x2E; keys["Pause"] = 0x13; keys["ScrollLock"] = 0x91;
        keys["Multiply"] = 0x6A; keys["Add"] = 0x6B; keys["Subtract"] = 0x6D; keys["Divide"] = 0x6F;
        keys["Comma"] = 0xBC; keys["Period"] = 0xBE; keys["Minus"] = 0xBD; keys["Plus"] = 0xBB;
        keys["LeftBracket"] = 0xDB; keys["RightBracket"] = 0xDD; keys["Backslash"] = 0xDC; keys["Tilde"] = 0xC0;
        return keys;
    }

    public static IEnumerable<string> KeyNames => Keys.Keys;

    public static bool TryParse(string text, out Hotkey hotkey, out string error)
    {
        hotkey = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty shortcut";
            return false;
        }
        bool ctrl = false, alt = false, shift = false, win = false;
        string key = null;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win": case "windows": win = true; break;
                case "":
                    error = $"'{text}': empty part";
                    return false;
                default:
                    if (key != null)
                    {
                        error = $"'{text}': more than one key ('{key}' and '{part}')";
                        return false;
                    }
                    key = part;
                    break;
            }
        }
        if (key == null)
        {
            error = $"'{text}': modifiers only, no key";
            return false;
        }
        if (!Keys.TryGetValue(key, out int vk))
        {
            error = $"'{text}': unknown key '{key}'";
            return false;
        }
        // Canonical spelling of the key name.
        string canonical = Keys.Keys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        hotkey = new Hotkey(ctrl, alt, shift, win, canonical, vk);
        return true;
    }
}

/// <summary>A set of controller buttons that must be held together, written like "Back+RightThumb".</summary>
public readonly record struct ControllerChord(ushort Mask, string Text)
{
    // XInput button bits.
    private static readonly Dictionary<string, ushort> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DpadUp"] = 0x0001, ["DpadDown"] = 0x0002, ["DpadLeft"] = 0x0004, ["DpadRight"] = 0x0008,
        ["Start"] = 0x0010, ["Back"] = 0x0020, ["LeftThumb"] = 0x0040, ["RightThumb"] = 0x0080,
        ["LeftShoulder"] = 0x0100, ["RightShoulder"] = 0x0200,
        ["A"] = 0x1000, ["B"] = 0x2000, ["X"] = 0x4000, ["Y"] = 0x8000,
    };

    public static IEnumerable<string> ButtonNames => Buttons.Keys;

    public bool IsHeld(ushort buttons) => Mask != 0 && (buttons & Mask) == Mask;

    public static bool TryParse(string text, out ControllerChord chord, out string error)
    {
        chord = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty chord";
            return false;
        }
        ushort mask = 0;
        var names = new List<string>();
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (!Buttons.TryGetValue(part, out ushort bit))
            {
                error = $"'{text}': unknown controller button '{part}'";
                return false;
            }
            mask |= bit;
            names.Add(Buttons.Keys.First(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase)));
        }
        if (names.Distinct().Count() < 2)
        {
            error = $"'{text}': a chord needs at least two different buttons, so a single press in the game cannot trigger it";
            return false;
        }
        chord = new ControllerChord(mask, string.Join("+", names.Distinct()));
        return true;
    }
}

/// <summary>
/// The tracker's per-user settings: what to show and which keys do what. Stored as JSON in the
/// user's application-data folder, outside the repository. Unknown or broken values fall back to
/// the defaults one by one, so a hand-edited file never stops the tracker from starting.
/// </summary>
public sealed class TrackerSettings
{
    /// <summary>Categories that are hidden. Everything else is shown.</summary>
    public List<string> HiddenCategories { get; set; } = new();

    /// <summary>Show collected treasures dimmed instead of hiding them.</summary>
    public bool ShowCollected { get; set; }

    public bool ShowBonfires { get; set; } = true;

    public Corner MinimapCorner { get; set; } = Corner.TopRight;
    /// <summary>Side of the minimap square in screen pixels.</summary>
    public int MinimapSize { get; set; } = 340;
    public int MinimapMargin { get; set; } = 24;
    /// <summary>0.2 (faint) to 1 (solid).</summary>
    public double MinimapOpacity { get; set; } = 0.85;
    /// <summary>Screen pixels per metre on the minimap.</summary>
    public double MinimapPixelsPerMeter { get; set; } = 4.0;
    /// <summary>How many of the nearest markers get a name label on the minimap.</summary>
    public int MinimapLabels { get; set; } = 4;

    // Defaults avoid the game's own keyboard bindings (letters, digits, arrows, Shift, Ctrl, Alt,
    // Tab, Esc, Space) and the usual launcher keys (F12, Shift+Tab).
    public string FullMapHotkey { get; set; } = "F7";
    public string LayerUpHotkey { get; set; } = "Ctrl+PageUp";
    public string LayerDownHotkey { get; set; } = "Ctrl+PageDown";
    public string SettingsHotkey { get; set; } = "F8";
    public string ToggleOverlayHotkey { get; set; } = "F9";
    /// <summary>Drops a "something is wrong here" note into feedback.log.</summary>
    public string MarkFeedbackHotkey { get; set; } = "F10";

    /// <summary>Name of the theme file (without .json) in the data folder's themes folder: "parchment" or "plain".</summary>
    public string Theme { get; set; } = "parchment";

    /// <summary>Optional controller chord for the full map. Off by default: the game receives the same buttons.</summary>
    public bool ControllerChordEnabled { get; set; }
    public string FullMapControllerChord { get; set; } = "Back+RightThumb";

    [JsonIgnore]
    public List<string> Problems { get; } = new();

    public bool IsCategoryShown(string category) => !HiddenCategories.Contains(category ?? ItemCategories.Unknown, StringComparer.OrdinalIgnoreCase);

    public void SetCategoryShown(string category, bool shown)
    {
        HiddenCategories.RemoveAll(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        if (!shown)
            HiddenCategories.Add(category);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Parses settings text. Never throws: whatever cannot be used is replaced by its default and listed in <see cref="Problems"/>.</summary>
    public static TrackerSettings Parse(string json)
    {
        TrackerSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<TrackerSettings>(json, JsonOpts) ?? new TrackerSettings();
        }
        catch (JsonException ex)
        {
            settings = new TrackerSettings();
            settings.Problems.Add("settings file could not be read, using defaults: " + ex.Message);
            return settings;
        }
        settings.Validate();
        return settings;
    }

    /// <summary>Clamps numbers into range and replaces unusable shortcuts with their defaults.</summary>
    public void Validate()
    {
        var defaults = new TrackerSettings();
        MinimapSize = Math.Clamp(MinimapSize, 120, 1200);
        MinimapMargin = Math.Clamp(MinimapMargin, 0, 400);
        MinimapOpacity = double.IsFinite(MinimapOpacity) ? Math.Clamp(MinimapOpacity, 0.2, 1.0) : defaults.MinimapOpacity;
        MinimapPixelsPerMeter = double.IsFinite(MinimapPixelsPerMeter) ? Math.Clamp(MinimapPixelsPerMeter, 0.5, 30) : defaults.MinimapPixelsPerMeter;
        MinimapLabels = Math.Clamp(MinimapLabels, 0, 30);
        HiddenCategories ??= new List<string>();
        HiddenCategories = HiddenCategories.Where(c => ItemCategories.All.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.ToLowerInvariant()).Distinct().ToList();

        FullMapHotkey = CheckHotkey(nameof(FullMapHotkey), FullMapHotkey, defaults.FullMapHotkey);
        LayerUpHotkey = CheckHotkey(nameof(LayerUpHotkey), LayerUpHotkey, defaults.LayerUpHotkey);
        LayerDownHotkey = CheckHotkey(nameof(LayerDownHotkey), LayerDownHotkey, defaults.LayerDownHotkey);
        SettingsHotkey = CheckHotkey(nameof(SettingsHotkey), SettingsHotkey, defaults.SettingsHotkey);
        ToggleOverlayHotkey = CheckHotkey(nameof(ToggleOverlayHotkey), ToggleOverlayHotkey, defaults.ToggleOverlayHotkey);
        MarkFeedbackHotkey = CheckHotkey(nameof(MarkFeedbackHotkey), MarkFeedbackHotkey, defaults.MarkFeedbackHotkey);
        if (string.IsNullOrWhiteSpace(Theme) || Theme.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            Theme = defaults.Theme;

        // Two actions on one shortcut: only the first could ever fire.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in new[]
                 {
                     (nameof(FullMapHotkey), FullMapHotkey), (nameof(LayerUpHotkey), LayerUpHotkey), (nameof(LayerDownHotkey), LayerDownHotkey),
                     (nameof(SettingsHotkey), SettingsHotkey), (nameof(ToggleOverlayHotkey), ToggleOverlayHotkey),
                     (nameof(MarkFeedbackHotkey), MarkFeedbackHotkey),
                 })
        {
            if (seen.TryGetValue(value, out string other))
                Problems.Add($"{name} and {other} are both '{value}'; only {other} will work");
            else
                seen[value] = name;
        }

        if (!ControllerChord.TryParse(FullMapControllerChord, out var chord, out string chordError))
        {
            Problems.Add($"{nameof(FullMapControllerChord)}: {chordError}; using '{defaults.FullMapControllerChord}'");
            FullMapControllerChord = defaults.FullMapControllerChord;
        }
        else
        {
            FullMapControllerChord = chord.Text;
        }
    }

    private string CheckHotkey(string name, string value, string fallback)
    {
        if (Hotkey.TryParse(value, out var hotkey, out string error))
            return hotkey.ToString();
        Problems.Add($"{name}: {error}; using '{fallback}'");
        return fallback;
    }

    /// <summary>Loads the file, or returns defaults when there is none.</summary>
    public static TrackerSettings Load(string path)
    {
        if (!File.Exists(path))
            return new TrackerSettings();
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            var settings = new TrackerSettings();
            settings.Problems.Add("settings file could not be opened: " + ex.Message);
            return settings;
        }
    }

    public void Save(string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }
}
