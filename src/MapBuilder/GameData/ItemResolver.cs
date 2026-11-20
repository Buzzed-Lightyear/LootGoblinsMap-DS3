using MapCore;

namespace MapBuilder.GameData;

/// <summary>
/// Field offsets inside the param rows this tool reads, with the row size each layout implies.
/// Taken from the community layout definitions in soulsmods/Paramdex (DS3/Defs) and checked
/// against the row sizes of the real files at build time: a mismatch stops the build instead of
/// producing wrong names.
/// </summary>
public static class ParamLayouts
{
    public static class ItemLot
    {
        public const string Name = "ItemLotParam";
        public const int RowSize = 0x98;
        public const int Slots = 8;
        public const int ItemId = 0x00;          // s32 × 8
        public const int ItemCategory = 0x20;    // u32 × 8
        public const int BasePoint = 0x40;       // s16 × 8, the slot's share of the draw
        public const int GetItemFlagId = 0x80;   // s32, set when the lot has been picked up
        public const int ItemCount = 0x8A;       // u8 × 8
    }

    public static class Goods
    {
        public const string Name = "EquipParamGoods";
        public const int RowSize = 0x80;
        public const int RefId = 0x00;           // s32, the effect applied when used
        public const int GoodsType = 0x3A;       // u8
    }

    public static class SpEffect
    {
        public const string Name = "SpEffectParam";
        public const int RowSize = 0x320;
        public const int Soul = 0xEC;            // s32, souls granted
    }

    public static class BonfireWarp
    {
        public const string Name = "BonfireWarpParam";
        public const int RowSize = 0x40;
        public const int LitFlag = 0x00;         // s32 "LocationEventId"
        public const int EntityId = 0x04;        // s32 "WarpEventId": the bonfire's entity id in the map
        public const int NameId = 0x08;          // s32, id in the menu text
        public const int OnlineAreaId = 0x18;    // s32, the play region; its first four digits are the place-name id
    }

    public static ParamFile Checked(Regulation regulation, string name, int expectedRowSize)
    {
        var param = regulation.GetParam(name);
        if (param.RowSize != expectedRowSize)
            throw new InvalidDataException(
                $"{name}: rows are 0x{param.RowSize:X} bytes, the known layout is 0x{expectedRowSize:X}. The game data has a different version; refusing to guess.");
        return param;
    }
}

/// <summary>What a bonfire is called and which flag says it has been lit.</summary>
public readonly record struct BonfireInfo(string Name, int LitFlag);

/// <summary>
/// Turns the ids found in the maps into names, categories and flags, using the game's own
/// params and text. Anything that cannot be resolved is reported as unresolved, never guessed.
/// </summary>
public sealed class ItemResolver
{
    // The item-category field of a lot slot.
    private const uint CategoryWeapon = 0x00000000, CategoryProtector = 0x10000000, CategoryAccessory = 0x20000000, CategoryGoods = 0x40000000;

    /// <summary>How far a chain of consecutive lot ids is followed.</summary>
    private const int MaxChain = 16;

    private readonly ParamFile _lots;
    private readonly ParamFile _goods;
    private readonly ParamFile _spEffects;
    private readonly GameText _text;
    private readonly Dictionary<int, (int NameId, int LitFlag, int OnlineAreaId)> _bonfires = new();

    public ItemResolver(ParamFile lots, ParamFile goods, ParamFile spEffects, ParamFile bonfires, GameText text)
    {
        _lots = lots;
        _goods = goods;
        _spEffects = spEffects;
        _text = text;
        if (bonfires != null)
            foreach (var row in bonfires.Rows)
                _bonfires.TryAdd(row.S32(ParamLayouts.BonfireWarp.EntityId),
                                 (row.S32(ParamLayouts.BonfireWarp.NameId), row.S32(ParamLayouts.BonfireWarp.LitFlag),
                                  row.S32(ParamLayouts.BonfireWarp.OnlineAreaId)));
    }

    public static ItemResolver Load(Regulation regulation, GameText text, TextWriter log)
    {
        var lots = ParamLayouts.Checked(regulation, ParamLayouts.ItemLot.Name, ParamLayouts.ItemLot.RowSize);
        var goods = ParamLayouts.Checked(regulation, ParamLayouts.Goods.Name, ParamLayouts.Goods.RowSize);
        var spEffects = ParamLayouts.Checked(regulation, ParamLayouts.SpEffect.Name, ParamLayouts.SpEffect.RowSize);
        var bonfires = ParamLayouts.Checked(regulation, ParamLayouts.BonfireWarp.Name, ParamLayouts.BonfireWarp.RowSize);
        log.WriteLine($"Regulation: {lots.RowCount} item lots, {goods.RowCount} goods, {spEffects.RowCount} effects, {bonfires.RowCount} bonfire rows.");
        return new ItemResolver(lots, goods, spEffects, bonfires, text);
    }

    /// <summary>Name and lit flag of the bonfire with this entity id, if the game lists it.</summary>
    public bool TryGetBonfire(int entityId, out BonfireInfo info)
    {
        if (_bonfires.TryGetValue(entityId, out var b))
        {
            info = new BonfireInfo(_text.Get(TextKind.Menu, b.NameId), b.LitFlag);
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>
    /// How many bonfires the game's table lists for a map. Entity ids start with the map's
    /// area and block (3001950 is in m30_00), which is what ties a row to a map.
    /// </summary>
    public int BonfiresListedFor(string mapId)
    {
        if (!MapIds.TryParse(mapId, out int area, out int block))
            return 0;
        return _bonfires.Keys.Count(entity => entity / 10000 == area * 10 + block);
    }

    /// <summary>
    /// The name the game shows for a map. Place names are keyed by play region: the first four
    /// digits of a bonfire's play-region id are the place-name id (300001 → 3000). The name most of
    /// the map's bonfires agree on is used; a map without listed bonfires falls back to the id
    /// formed from its area and block.
    /// </summary>
    public string AreaName(string mapId)
    {
        if (!MapIds.TryParse(mapId, out int area, out int block))
            return null;

        var byBonfires = _bonfires
            .Where(kv => kv.Key / 10000 == area * 10 + block && kv.Value.OnlineAreaId > 0)
            .Select(kv => kv.Value.OnlineAreaId / 100)
            .GroupBy(id => id)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => _text.Get(TextKind.Place, g.Key))
            .FirstOrDefault(name => name != null);
        if (byBonfires != null)
            return byBonfires;
        string byMapId = _text.Get(TextKind.Place, area * 100 + block * 10) ?? _text.Get(TextKind.Place, area * 100);
        if (byMapId != null)
            return byMapId;

        // Last resort: the map's first bonfire (lowest name id) usually carries the area's name.
        return _bonfires
            .Where(kv => kv.Key / 10000 == area * 10 + block)
            .OrderBy(kv => kv.Value.NameId)
            .Select(kv => _text.Get(TextKind.Menu, kv.Value.NameId))
            .FirstOrDefault(name => name != null);
    }

    /// <summary>Whether the regulation has this item lot at all. Treasure events pointing at a missing lot are unused leftovers.</summary>
    public bool LotExists(int lotId) => lotId > 0 && _lots.Contains(lotId);

    /// <summary>
    /// Resolves the lots attached to one treasure. Each lot is followed through the consecutive
    /// ids that continue it; every slot with an item and a share of the draw becomes an item.
    /// </summary>
    public ResolvedTreasure Resolve(IEnumerable<int> lotIds)
    {
        var result = new ResolvedTreasure();
        foreach (int first in lotIds)
        {
            if (first <= 0)
                continue;
            if (!_lots.Contains(first))
            {
                result.Unresolved = true;
                result.Notes.Add($"item lot {first} is not in the regulation");
                continue;
            }

            for (int id = first, step = 0; step < MaxChain && _lots.TryGetRow(id, out var row); id++, step++)
            {
                int flag = row.S32(ParamLayouts.ItemLot.GetItemFlagId);
                if (flag > 0 && result.Flag <= 0)
                    result.Flag = flag;
                else if (flag > 0 && flag != result.Flag && !result.OtherFlags.Contains(flag))
                    result.OtherFlags.Add(flag);

                var slots = new List<(int Item, uint Category, int Points, int Count)>();
                for (int s = 0; s < ParamLayouts.ItemLot.Slots; s++)
                {
                    int item = row.S32(ParamLayouts.ItemLot.ItemId + 4 * s);
                    int points = row.S16(ParamLayouts.ItemLot.BasePoint + 2 * s);
                    if (item <= 0 || points <= 0)
                        continue;
                    slots.Add((item, row.U32(ParamLayouts.ItemLot.ItemCategory + 4 * s), points, row.U8(ParamLayouts.ItemLot.ItemCount + s)));
                }
                int total = slots.Sum(s => s.Points);
                foreach (var slot in slots)
                {
                    var item = Describe(slot.Item, slot.Category);
                    item.Count = Math.Max(1, slot.Count);
                    // Several slots share one draw: each is only a chance, not a certainty.
                    item.ChancePercent = slots.Count > 1 ? (int)Math.Round(100.0 * slot.Points / total) : 100;
                    if (item.Name == null)
                    {
                        result.Unresolved = true;
                        result.Notes.Add($"no name for item {slot.Item} (category 0x{slot.Category:X8}) in lot {id}");
                    }
                    result.Items.Add(item);
                }
            }
        }

        if (result.Items.Count == 0 && !result.Unresolved)
        {
            result.Unresolved = true;
            result.Notes.Add("the lot holds no items");
        }
        return result;
    }

    private MapDocument.ItemInfo Describe(int itemId, uint category)
    {
        var item = new MapDocument.ItemInfo { ItemId = itemId, Category = ItemCategories.Unknown };
        switch (category)
        {
            case CategoryWeapon:
            {
                // Reinforcement adds 1..10 and an infusion adds a multiple of 100 to the base id.
                int level = itemId % 100;
                string name = _text.Get(TextKind.Weapon, itemId)
                              ?? _text.Get(TextKind.Weapon, itemId - level)
                              ?? _text.Get(TextKind.Weapon, itemId - itemId % 10000);
                if (name != null && level > 0 && _text.Get(TextKind.Weapon, itemId) == null)
                    name += $" +{level}";
                item.Name = name;
                item.Category = ItemCategories.Weapon;
                break;
            }
            case CategoryProtector:
                item.Name = _text.Get(TextKind.Protector, itemId);
                item.Category = ItemCategories.Armor;
                break;
            case CategoryAccessory:
                item.Name = _text.Get(TextKind.Accessory, itemId);
                item.Category = ItemCategories.Ring;
                break;
            case CategoryGoods:
                item.Name = _text.Get(TextKind.Goods, itemId);
                item.Category = GoodsCategory(itemId);
                break;
        }
        return item;
    }

    /// <summary>Goods are split by the game's own goods type; souls are the consumables whose effect grants souls.</summary>
    private string GoodsCategory(int goodsId)
    {
        if (!_goods.TryGetRow(goodsId, out var row))
            return ItemCategories.Consumable;
        switch (row.U8(ParamLayouts.Goods.GoodsType))
        {
            case 1: return ItemCategories.Key;
            case 2: return ItemCategories.Upgrade;
            case 5: return ItemCategories.Spell;
        }
        int effect = row.S32(ParamLayouts.Goods.RefId);
        if (effect > 0 && _spEffects != null && _spEffects.TryGetRow(effect, out var fx) && fx.S32(ParamLayouts.SpEffect.Soul) > 0)
            return ItemCategories.Soul;
        return ItemCategories.Consumable;
    }
}

/// <summary>Everything one treasure marker holds.</summary>
public sealed class ResolvedTreasure
{
    public List<MapDocument.ItemInfo> Items { get; } = new();
    /// <summary>The flag that is set once the treasure has been picked up; 0 when the lot has none.</summary>
    public int Flag { get; set; }
    /// <summary>Flags of further lots in the chain when they differ from <see cref="Flag"/>.</summary>
    public List<int> OtherFlags { get; } = new();
    public bool Unresolved { get; set; }
    public List<string> Notes { get; } = new();
}
