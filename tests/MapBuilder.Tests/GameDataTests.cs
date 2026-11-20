using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using MapBuilder.Checks;
using MapBuilder.GameData;
using MapBuilder.Pipeline;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SoulsFormats;

namespace MapBuilder.Tests;

/// <summary>
/// Param and text parsing and the lot-to-item resolution, on synthetic data only: every name,
/// id and flag here is made up for the test.
/// </summary>
[TestClass]
public class GameDataTests
{
    [ClassInitialize]
    public static void Init(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ---- synthetic files ----------------------------------------------------------------

    /// <summary>Builds a param file the way Dark Souls III stores them: 64-bit row offsets, param type as a trailing string.</summary>
    private static byte[] BuildParam(string type, int rowSize, params (int Id, Action<byte[]> Fill)[] rows)
    {
        const int header = 0x40, rowHeader = 24;
        int dataStart = header + rows.Length * rowHeader;
        int stringsStart = dataStart + rows.Length * rowSize;
        byte[] typeBytes = Encoding.ASCII.GetBytes(type + "\0");
        var file = new byte[stringsStart + typeBytes.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(file, (uint)stringsStart);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(0x0A), (ushort)rows.Length);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x10), stringsStart);
        file[0x2D] = 0x84;   // long data offsets + param type stored as an offset
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(0x30), dataStart);
        for (int i = 0; i < rows.Length; i++)
        {
            int at = header + i * rowHeader;
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(at), rows[i].Id);
            BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(at + 8), dataStart + i * rowSize);
            var row = new byte[rowSize];
            rows[i].Fill(row);
            row.CopyTo(file, dataStart + i * rowSize);
        }
        typeBytes.CopyTo(file, stringsStart);
        return file;
    }

    private static void S32(byte[] row, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(offset), value);
    private static void U32(byte[] row, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(offset), value);
    private static void S16(byte[] row, int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(row.AsSpan(offset), value);

    private const uint Weapon = 0x00000000, Protector = 0x10000000, Accessory = 0x20000000, Goods = 0x40000000;

    /// <summary>One item-lot row: up to eight (item, category, points, count) slots and a pickup flag.</summary>
    private static (int, Action<byte[]>) Lot(int id, int flag, params (int Item, uint Category, short Points, byte Count)[] slots) => (id, row =>
    {
        for (int s = 0; s < slots.Length; s++)
        {
            S32(row, ParamLayouts.ItemLot.ItemId + 4 * s, slots[s].Item);
            U32(row, ParamLayouts.ItemLot.ItemCategory + 4 * s, slots[s].Category);
            S16(row, ParamLayouts.ItemLot.BasePoint + 2 * s, slots[s].Points);
            row[ParamLayouts.ItemLot.ItemCount + s] = slots[s].Count;
        }
        S32(row, ParamLayouts.ItemLot.GetItemFlagId, flag);
    });

    private static (int, Action<byte[]>) GoodsRow(int id, byte goodsType, int effect = -1) => (id, row =>
    {
        S32(row, ParamLayouts.Goods.RefId, effect);
        row[ParamLayouts.Goods.GoodsType] = goodsType;
    });

    private static (int, Action<byte[]>) Effect(int id, int souls) => (id, row => S32(row, ParamLayouts.SpEffect.Soul, souls));

    private static (int, Action<byte[]>) BonfireRow(int rowId, int entity, int litFlag, int nameId, int onlineArea) => (rowId, row =>
    {
        S32(row, ParamLayouts.BonfireWarp.LitFlag, litFlag);
        S32(row, ParamLayouts.BonfireWarp.EntityId, entity);
        S32(row, ParamLayouts.BonfireWarp.NameId, nameId);
        S32(row, ParamLayouts.BonfireWarp.OnlineAreaId, onlineArea);
    });

    /// <summary>A message file as the game stores it, built with the same library that reads it.</summary>
    private static byte[] BuildFmg(params (int Id, string Text)[] entries)
    {
        var fmg = new FMG(FMG.FMGVersion.DarkSouls3);
        foreach (var (id, text) in entries)
            fmg.Entries.Add(new FMG.Entry(id, text));
        return fmg.Write();
    }

    private static GameText Text()
    {
        var text = new GameText();
        text.AddFmg(10, BuildFmg((500, "Test Tonic"), (510, "Test Key"), (520, "Test Shard"), (530, "Test Spell Scroll"), (540, "Soul of a Test Subject")));
        text.AddFmg(250, BuildFmg((550, "Late Addition Tonic"), (500, "  "), (9999, null)));   // DLC file: adds one, blank entries hide nothing
        text.AddFmg(11, BuildFmg((2000000, "Test Sword"), (2000100, "Sharp Test Sword")));
        text.AddFmg(12, BuildFmg((70000, "Test Helm")));
        text.AddFmg(13, BuildFmg((300, "Test Ring"), (301, "Test Ring+1")));
        text.AddFmg(19, BuildFmg((7000, "Test Keep"), (7010, "Test Annex"), (7100, "Test Cellar")));
        text.AddFmg(200, BuildFmg((900100, "Keep Gate"), (900101, "Keep Roof"), (900200, "Lonely Camp")));
        text.AddFmg(4242, BuildFmg((1, "ignored: not a file the tool reads")));
        return text;
    }

    private static ItemResolver Resolver(params (int, Action<byte[]>)[] lots)
    {
        var lotParam = ParamFile.Read(BuildParam("ITEMLOT_PARAM_ST", ParamLayouts.ItemLot.RowSize, lots));
        var goods = ParamFile.Read(BuildParam("EQUIP_PARAM_GOODS_ST", ParamLayouts.Goods.RowSize,
            GoodsRow(500, 0), GoodsRow(510, 1), GoodsRow(520, 2), GoodsRow(530, 5), GoodsRow(540, 0, effect: 8000), GoodsRow(550, 0, effect: 8001)));
        var effects = ParamFile.Read(BuildParam("SP_EFFECT_PARAM_ST", ParamLayouts.SpEffect.RowSize, Effect(8000, 2000), Effect(8001, 0)));
        var bonfires = ParamFile.Read(BuildParam("BONFIRE_WARP_PARAM_ST", ParamLayouts.BonfireWarp.RowSize,
            BonfireRow(1, 7001950, 17000001, 900100, 700001),
            BonfireRow(2, 7001951, 17000002, 900101, 700002),
            BonfireRow(3, 7011950, 17010001, 900200, 0),
            BonfireRow(4, 7101950, 17100001, 999999, 0)));
        return new ItemResolver(lotParam, goods, effects, bonfires, Text());
    }

    // ---- param parsing ------------------------------------------------------------------

    [TestMethod]
    public void Param_ReadsRowsTypeAndFields()
    {
        byte[] bytes = BuildParam("TEST_PARAM_ST", 0x20,
            (10, row => { S32(row, 0, -5); S16(row, 4, 1234); row[6] = 200; BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(8), 1.5f); }),
            (20, row => S32(row, 0x1C, 77)),
            (20, row => S32(row, 0x1C, 88)));   // a duplicated id: the first row wins, as in the game

        var param = ParamFile.Read(bytes);
        Assert.AreEqual("TEST_PARAM_ST", param.ParamType);
        Assert.AreEqual(3, param.RowCount);
        Assert.AreEqual(0x20, param.RowSize);

        Assert.IsTrue(param.TryGetRow(10, out var first));
        Assert.AreEqual(-5, first.S32(0));
        Assert.AreEqual(1234, first.S16(4));
        Assert.AreEqual(200, first.U8(6));
        Assert.AreEqual(1.5f, first.F32(8));
        Assert.IsTrue(param.TryGetRow(20, out var second));
        Assert.AreEqual(77, second.S32(0x1C));
        Assert.IsFalse(param.TryGetRow(30, out _));
        Assert.IsTrue(param.Contains(20));

        // A field outside the row is a bug in the layout, not a silent read of the next row.
        Assert.ThrowsException<InvalidDataException>(() => first.S32(0x1E));
    }

    [TestMethod]
    public void Param_SingleRow_SizeComesFromTheStrings()
    {
        var param = ParamFile.Read(BuildParam("ONE_ROW", 0x98, (5, row => S32(row, 0x80, 42))));
        Assert.AreEqual(0x98, param.RowSize);
        Assert.IsTrue(param.TryGetRow(5, out var row));
        Assert.AreEqual(42, row.S32(0x80));
    }

    [TestMethod]
    public void Param_RejectsBrokenFiles()
    {
        Assert.ThrowsException<InvalidDataException>(() => ParamFile.Read(new byte[10]));
        byte[] truncated = BuildParam("T", 0x40, (1, _ => { }), (2, _ => { }))[..0x60];
        Assert.ThrowsException<InvalidDataException>(() => ParamFile.Read(truncated));
    }

    [TestMethod]
    public void Layout_MismatchStopsInsteadOfGuessing()
    {
        // A regulation whose item-lot rows are a different size than the known layout must not be read as if it matched.
        var odd = ParamFile.Read(BuildParam("ITEMLOT_PARAM_ST", 0xA0, Lot(1, 0), Lot(2, 0)));
        Assert.AreEqual(0xA0, odd.RowSize);
        Assert.AreNotEqual(ParamLayouts.ItemLot.RowSize, odd.RowSize);
    }

    [TestMethod]
    public void Regulation_DecryptsWhatItEncrypts_AndRejectsOtherData()
    {
        byte[] plain = Encoding.ASCII.GetBytes("BND4 would be here; this is only the cipher round trip. 0123456789");
        byte[] iv = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] encrypted = Regulation.Encrypt(plain, iv);
        CollectionAssert.AreEqual(iv, encrypted[..16]);
        CollectionAssert.AreNotEqual(plain, encrypted[16..(16 + plain.Length)]);
        byte[] back = Regulation.Decrypt(encrypted);
        CollectionAssert.AreEqual(plain, back[..plain.Length]);

        // Wrong length, and decrypted bytes that are not a binder.
        Assert.ThrowsException<InvalidDataException>(() => Regulation.Decrypt(new byte[33]));
        Assert.ThrowsException<InvalidDataException>(() => Regulation.FromEncrypted(encrypted));
    }

    // ---- text parsing -------------------------------------------------------------------

    [TestMethod]
    public void Text_ReadsMessageFiles_MergesDlcAdditions_IgnoresBlanks()
    {
        var text = Text();
        Assert.AreEqual("Test Tonic", text.Get(TextKind.Goods, 500), "a blank DLC entry must not hide the base text");
        Assert.AreEqual("Late Addition Tonic", text.Get(TextKind.Goods, 550));
        Assert.AreEqual("Test Sword", text.Get(TextKind.Weapon, 2000000));
        Assert.AreEqual("Test Keep", text.Get(TextKind.Place, 7000));
        Assert.AreEqual("Keep Gate", text.Get(TextKind.Menu, 900100));
        Assert.IsNull(text.Get(TextKind.Goods, 9999));
        Assert.IsNull(text.Get(TextKind.Goods, 123456));
        Assert.AreEqual(6, text.Count(TextKind.Goods));
    }

    // ---- lots → items -------------------------------------------------------------------

    [TestMethod]
    public void Lot_SingleItem_ResolvesNameCategoryCountAndFlag()
    {
        var resolver = Resolver(Lot(1000, 57000100, (500, Goods, 1000, 3)));
        var treasure = resolver.Resolve(new[] { 1000 });

        Assert.IsFalse(treasure.Unresolved);
        Assert.AreEqual(57000100, treasure.Flag);
        Assert.AreEqual(1, treasure.Items.Count);
        Assert.AreEqual("Test Tonic", treasure.Items[0].Name);
        Assert.AreEqual(ItemCategories.Consumable, treasure.Items[0].Category);
        Assert.AreEqual(3, treasure.Items[0].Count);
        Assert.AreEqual(100, treasure.Items[0].ChancePercent);
    }

    [TestMethod]
    public void Lot_Chain_FollowsConsecutiveIds_AndStopsAtTheGap()
    {
        var resolver = Resolver(
            Lot(2000, 57000200, (2000000, Weapon, 1000, 1)),
            Lot(2001, 0, (70000, Protector, 1000, 1)),
            Lot(2002, 57000202, (300, Accessory, 1000, 1)),
            Lot(2004, 57000204, (510, Goods, 1000, 1)));   // 2003 is missing: not part of the chain

        var treasure = resolver.Resolve(new[] { 2000 });
        CollectionAssert.AreEqual(new[] { "Test Sword", "Test Helm", "Test Ring" }, treasure.Items.Select(i => i.Name).ToArray());
        CollectionAssert.AreEqual(new[] { ItemCategories.Weapon, ItemCategories.Armor, ItemCategories.Ring }, treasure.Items.Select(i => i.Category).ToArray());
        Assert.AreEqual(57000200, treasure.Flag, "the first lot's flag is the marker's flag");
        CollectionAssert.AreEqual(new[] { 57000202 }, treasure.OtherFlags);
        Assert.IsFalse(treasure.Unresolved);
    }

    [TestMethod]
    public void Lot_TwoLotsOnOneTreasure_AreMerged()
    {
        var resolver = Resolver(Lot(3000, 57000300, (500, Goods, 1000, 1)), Lot(3500, 57000350, (520, Goods, 1000, 2)));
        var treasure = resolver.Resolve(new[] { 3000, 3500, -1, 0 });
        CollectionAssert.AreEqual(new[] { "Test Tonic", "Test Shard" }, treasure.Items.Select(i => i.Name).ToArray());
        Assert.AreEqual(57000300, treasure.Flag);
        Assert.AreEqual(2, treasure.Items[1].Count);
    }

    [TestMethod]
    public void Lot_SeveralSlotsShareADraw_EachIsOnlyAChance()
    {
        var resolver = Resolver(Lot(4000, 57000400, (500, Goods, 750, 1), (520, Goods, 250, 1), (530, Goods, 0, 1)));
        var treasure = resolver.Resolve(new[] { 4000 });
        Assert.AreEqual(2, treasure.Items.Count, "a slot with no share of the draw is not an item");
        Assert.AreEqual(75, treasure.Items[0].ChancePercent);
        Assert.AreEqual(25, treasure.Items[1].ChancePercent);
    }

    [TestMethod]
    public void Lot_Categories_ComeFromTheGameData()
    {
        var resolver = Resolver(Lot(5000, 1, (500, Goods, 1000, 1)), Lot(5010, 2, (510, Goods, 1000, 1)), Lot(5020, 3, (520, Goods, 1000, 1)),
                                Lot(5030, 4, (530, Goods, 1000, 1)), Lot(5040, 5, (540, Goods, 1000, 1)), Lot(5050, 6, (550, Goods, 1000, 1)));
        string Category(int lot) => resolver.Resolve(new[] { lot }).Items.Single().Category;

        Assert.AreEqual(ItemCategories.Consumable, Category(5000));
        Assert.AreEqual(ItemCategories.Key, Category(5010));
        Assert.AreEqual(ItemCategories.Upgrade, Category(5020));
        Assert.AreEqual(ItemCategories.Spell, Category(5030));
        Assert.AreEqual(ItemCategories.Soul, Category(5040), "a consumable whose effect grants souls");
        Assert.AreEqual(ItemCategories.Consumable, Category(5050), "an effect that grants no souls");

        // A marker with several items is shown under the most notable category.
        Assert.AreEqual(ItemCategories.Key, ItemCategories.Primary(new[] { ItemCategories.Consumable, ItemCategories.Key, ItemCategories.Weapon }));
        Assert.AreEqual(ItemCategories.Unknown, ItemCategories.Primary(Array.Empty<string>()));
    }

    [TestMethod]
    public void Lot_WeaponVariants_AreNamedFromTheBaseWeapon()
    {
        var resolver = Resolver(Lot(6000, 1, (2000000, Weapon, 1000, 1)), Lot(6010, 2, (2000003, Weapon, 1000, 1)),
                                Lot(6020, 3, (2000100, Weapon, 1000, 1)), Lot(6030, 4, (2000102, Weapon, 1000, 1)));
        string Name(int lot) => resolver.Resolve(new[] { lot }).Items.Single().Name;
        Assert.AreEqual("Test Sword", Name(6000));
        Assert.AreEqual("Test Sword +3", Name(6010));
        Assert.AreEqual("Sharp Test Sword", Name(6020));
        Assert.AreEqual("Sharp Test Sword +2", Name(6030));
    }

    [TestMethod]
    public void Lot_Unresolved_IsMarkedNotGuessed()
    {
        var resolver = Resolver(Lot(7000, 57000700, (777, Goods, 1000, 1)), Lot(7010, 57000710));

        var noName = resolver.Resolve(new[] { 7000 });
        Assert.IsTrue(noName.Unresolved);
        Assert.IsNull(noName.Items.Single().Name);
        StringAssert.Contains(noName.Notes.Single(), "no name for item 777");

        var empty = resolver.Resolve(new[] { 7010 });
        Assert.IsTrue(empty.Unresolved);
        Assert.AreEqual(0, empty.Items.Count);

        var missing = resolver.Resolve(new[] { 123456 });
        Assert.IsTrue(missing.Unresolved);
        StringAssert.Contains(missing.Notes[0], "not in the regulation");
        Assert.IsFalse(resolver.LotExists(123456));
        Assert.IsTrue(resolver.LotExists(7000));
    }

    [TestMethod]
    public void Markers_GetNamesFlagsAndCategories_UnusedEventsAreFlagged()
    {
        var resolver = Resolver(Lot(8000, 57000800, (510, Goods, 1000, 1)), Lot(8001, 0, (500, Goods, 1000, 2)));
        var markers = new List<Marker>
        {
            new() { Type = MapDocument.BonfireType, Name = "bonfire_a", EntityId = 7001950 },
            new() { Type = MapDocument.BonfireType, Name = "bonfire_unknown", EntityId = 7009999 },
            new() { Type = MapDocument.TreasureType, Name = "chest", EntityId = 1, ItemLots = { 8000 } },
            new() { Type = MapDocument.TreasureType, Name = "leftover", EntityId = 2, ItemLots = { 8800 } },
        };
        Markers.Resolve(markers, resolver);

        Assert.AreEqual("Keep Gate", markers[0].DisplayName);
        Assert.AreEqual(17000001, markers[0].Flag);
        Assert.IsFalse(markers[0].Unresolved);
        Assert.IsTrue(markers[1].Unresolved);
        Assert.IsNull(markers[1].DisplayName);

        Assert.AreEqual(ItemCategories.Key, markers[2].Category, "the key outranks the tonic that comes with it");
        Assert.AreEqual(57000800, markers[2].Flag);
        CollectionAssert.AreEqual(new[] { "Test Key", "Test Tonic" }, markers[2].Items.Select(i => i.Name).ToArray());
        Assert.IsFalse(markers[2].Unused);

        Assert.IsTrue(markers[3].Unused, "a treasure event whose lot does not exist cannot be picked up");
        Assert.IsTrue(markers[3].Unresolved);

        // Without game data every marker is left unresolved rather than invented.
        var bare = new List<Marker> { new() { Type = MapDocument.TreasureType, Name = "x", ItemLots = { 8000 } } };
        Markers.Resolve(bare, null);
        Assert.IsTrue(bare[0].Unresolved);
        Assert.AreEqual(0, bare[0].Items.Count);
    }

    [TestMethod]
    public void Names_AreaFromPlayRegion_ThenMapId_ThenFirstBonfire()
    {
        var resolver = Resolver(Lot(1, 0));
        Assert.AreEqual("Test Keep", resolver.AreaName("m70_00_00_00"), "play region 7000xx → place name 7000");
        Assert.AreEqual("Test Annex", resolver.AreaName("m70_01_00_00"), "no play region on its bonfire: area 70, block 1 → 7010");
        Assert.AreEqual("Test Cellar", resolver.AreaName("m71_00_00_00"), "area 71 → 7100");
        Assert.AreEqual("Test Keep", resolver.AreaName("m70_05_00_00"), "falls back to the area's first place name");
        Assert.IsNull(resolver.AreaName("m88_00_00_00"));
        Assert.IsNull(resolver.AreaName("nonsense"));

        Assert.AreEqual(2, resolver.BonfiresListedFor("m70_00_00_00"));
        Assert.AreEqual(1, resolver.BonfiresListedFor("m70_01_00_00"));
        Assert.AreEqual(0, resolver.BonfiresListedFor("m88_00_00_00"));

        Assert.IsTrue(resolver.TryGetBonfire(7011950, out var camp));
        Assert.AreEqual(new BonfireInfo("Lonely Camp", 17010001), camp);
        Assert.IsTrue(resolver.TryGetBonfire(7101950, out var unnamed));
        Assert.IsNull(unnamed.Name, "a name id without text stays without a name");
    }

    // ---- build-time geometry ------------------------------------------------------------

    private static WorldFace Quad(float x, float z, float size, float y)
    {
        var f = new WorldFace
        {
            Vertices = new[] { new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y, z + size), new Vector3(x, y, z + size) },
        };
        f.ComputeBounds();
        return f;
    }

    [TestMethod]
    public void MainBody_StrayPiecesFarAway_DoNotCountTowardsTheBounds()
    {
        var faces = new List<WorldFace>();
        for (int x = 0; x < 20; x++)
            for (int z = 0; z < 10; z++)
                faces.Add(Quad(x * 4, z * 4, 4, 0));          // the map: 80 × 40 m, 200 polygons
        faces.Add(Quad(400, 0, 6, 0));                         // one polygon 300 m away
        faces.Add(Quad(-300, 500, 3, 10));                     // and another
        // A second real section 120 m off, with enough polygons to matter.
        for (int i = 0; i < 12; i++)
            faces.Add(Quad(200 + i * 4, 0, 4, 0));

        int strays = MainBody.MarkStrays(faces);

        Assert.AreEqual(2, strays);
        Assert.IsTrue(faces[200].Stray && faces[201].Stray);
        Assert.IsTrue(faces.Take(200).All(f => !f.Stray));
        Assert.IsTrue(faces.Skip(202).All(f => !f.Stray), "a separate section with real floor space is part of the map");
        var body = faces.Where(f => !f.Stray).ToList();
        Assert.AreEqual(248f, body.Max(f => f.MaxX));
        Assert.AreEqual(0f, body.Min(f => f.MinX));
    }

    [TestMethod]
    public void Overlap_FindsMapsThatShareSpace_AndOnlyThose()
    {
        var maps = new Dictionary<string, List<WorldFace>>
        {
            ["m70_00_00_00"] = new() { Quad(0, 0, 10, 0), Quad(10, 0, 10, 0) },
            ["m70_01_00_00"] = new() { Quad(15, 5, 10, 1), Quad(100, 100, 10, 0) },   // overlaps the first, 1 m higher
            ["m71_00_00_00"] = new() { Quad(0, 0, 10, 40) },                           // same spot, 40 m up: different space
            ["m72_00_00_00"] = new() { Quad(500, 500, 10, 0) },                        // nowhere near
        };
        var overlaps = OverlapAnalysis.Run(maps);

        Assert.AreEqual(1, overlaps.Count);
        Assert.AreEqual("m70_00_00_00", overlaps[0].MapA);
        Assert.AreEqual("m70_01_00_00", overlaps[0].MapB);
        Assert.AreEqual(1, overlaps[0].PolygonsOfA);
        Assert.AreEqual(1, overlaps[0].PolygonsOfB);
        StringAssert.Contains(OverlapAnalysis.Describe(overlaps, maps.Count, new Dictionary<string, string>()), "1 of 6 map pairs share space");

        maps.Remove("m70_01_00_00");
        StringAssert.Contains(OverlapAnalysis.Describe(OverlapAnalysis.Run(maps), maps.Count, new Dictionary<string, string>()), "None of the 3 map pairs");
    }

    [TestMethod]
    public void MarkerDistanceToMesh_UsesAHeightWindow()
    {
        var faces = new List<WorldFace> { Quad(0, 0, 10, 0) };
        Assert.AreEqual(0f, Markers.DistanceToMesh(new Vector3(5, 1, 5), faces));
        Assert.AreEqual(3f, Markers.DistanceToMesh(new Vector3(13, 0, 5), faces), 1e-4f);
        Assert.AreEqual(float.MaxValue, Markers.DistanceToMesh(new Vector3(5, 20, 5), faces), "20 m above the floor is not on it");
    }
}
