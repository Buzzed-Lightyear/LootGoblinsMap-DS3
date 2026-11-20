using System.Globalization;

namespace MapCore;

/// <summary>
/// Map ids as the game writes them: "m30_00_00_00" is area 30, block 0. In memory the same id
/// is one 32-bit number, area in the top byte: 0x1E000000.
/// </summary>
public static class MapIds
{
    public static string Format(int area, int block, int region = 0, int index = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"m{area:D2}_{block:D2}_{region:D2}_{index:D2}");

    /// <summary>From the packed number the game keeps in memory.</summary>
    public static string FromPacked(uint packed) =>
        Format((int)(packed >> 24), (int)((packed >> 16) & 0xFF), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));

    public static bool TryParse(string mapId, out int area, out int block)
    {
        area = block = 0;
        if (string.IsNullOrEmpty(mapId) || mapId.Length != 12 || mapId[0] != 'm' || mapId[3] != '_' || mapId[6] != '_' || mapId[9] != '_')
            return false;
        return int.TryParse(mapId.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out area) &&
               int.TryParse(mapId.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out block) &&
               int.TryParse(mapId.AsSpan(7, 2), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(mapId.AsSpan(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    public static bool TryPack(string mapId, out uint packed)
    {
        packed = 0;
        if (!TryParse(mapId, out int area, out int block))
            return false;
        int region = int.Parse(mapId.AsSpan(7, 2), CultureInfo.InvariantCulture);
        int index = int.Parse(mapId.AsSpan(10, 2), CultureInfo.InvariantCulture);
        packed = (uint)(area << 24 | block << 16 | region << 8 | index);
        return true;
    }
}

/// <summary>The item categories markers are filtered and coloured by. Derived from the game data by the builder.</summary>
public static class ItemCategories
{
    public const string Key = "key";
    public const string Ring = "ring";
    public const string Weapon = "weapon";
    public const string Armor = "armor";
    public const string Spell = "spell";
    public const string Upgrade = "upgrade";
    public const string Soul = "soul";
    public const string Consumable = "consumable";
    public const string Unknown = "unknown";

    /// <summary>All categories, most notable first: a marker with several items takes the first that applies.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Key, Ring, Weapon, Armor, Spell, Upgrade, Soul, Consumable, Unknown };

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [Key] = "Key items",
        [Ring] = "Rings",
        [Weapon] = "Weapons",
        [Armor] = "Armor",
        [Spell] = "Spells",
        [Upgrade] = "Upgrade materials",
        [Soul] = "Souls",
        [Consumable] = "Consumables",
        [Unknown] = "Unresolved",
    };

    /// <summary>The category a marker is shown under, given the categories of its items.</summary>
    public static string Primary(IEnumerable<string> categories)
    {
        var set = categories.ToHashSet();
        foreach (string c in All)
            if (set.Contains(c))
                return c;
        return Unknown;
    }
}
