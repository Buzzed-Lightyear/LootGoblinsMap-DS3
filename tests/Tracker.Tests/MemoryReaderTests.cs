using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SoulMemory.DarkSouls3;
using SoulMemory.Memory;

namespace Tracker.Tests;

/// <summary>
/// The vendored pointer code, exercised against a fake process image: a byte array standing in
/// for the game's memory, with the player scan pattern and the pointer chain planted in it.
/// </summary>
[TestClass]
public class MemoryReaderTests
{
    /// <summary>A flat block of "process memory" starting at a base address. Read-only, like the real source.</summary>
    private sealed class FakeMemory(long baseAddress, byte[] bytes) : IMemorySource
    {
        public int Reads;

        public bool TryRead(long address, Span<byte> buffer)
        {
            Reads++;
            long offset = address - baseAddress;
            if (offset < 0 || offset + buffer.Length > bytes.Length)
                return false;
            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
            return true;
        }
    }

    private const long Base = 0x1_4000_0000;

    private static readonly byte[] PlayerPattern = Bytes("48 8b 0d 00 00 00 00 45 33 c0 48 8d 55 e7 e8 11 22 33 44 0f 2f 73 70 72 0d f3 01 02 03 04 05 06 07 08 0f 11 43 70");
    private static readonly byte[] LoadingPattern = Bytes("c6 05 00 00 00 00 01 e8 aa bb cc dd 84 c0 0f 94 c0 e9");

    private static readonly byte[] EventFlagPattern = Bytes("48 c7 05 00 00 00 00 00 00 00 00 48 8b 7c 24 38 c7 46 54 ff ff ff ff 48 83 c4 20 5e c3");
    private static readonly byte[] FieldAreaPattern = Bytes("4c 8b 3d 00 00 00 00 8b 45 87 83 f8 ff 74 69 48 8d 4d 8f 48 89 4d 9f 89 45 8f 48 8d 55 8f 49 8b 4f 10");

    // Where the synthetic event-flag words live (offsets into the image).
    private const int AreaFlagWords = 0xE000;     // flags 1300 0xxx (area 30, block 0, thousand 0)
    private const int GlobalFlagWords = 0xE100;   // flags 6xxx (no area: category 0, thousand 6)

    /// <summary>
    /// Adds what the event-flag walk reads: the two scans, FieldArea → world info (area 30) →
    /// block info (m30_00, category 4), and SprjEventFlagMan → group table → flag words.
    /// </summary>
    private static void AddEventFlagStructures(byte[] image)
    {
        const int flagScanAt = 0x3000, areaScanAt = 0x4000, flagManSlot = 0x8020, fieldAreaSlot = 0x8010;
        const int fieldArea = 0xC000, worldInfoOwner = 0xC100, worldInfos = 0xC200, blocks = 0xC400;
        const int flagMan = 0xD000, groups = 0xD400, group0 = 0xDC00, group1 = 0xD800;

        EventFlagPattern.CopyTo(image, flagScanAt);
        Put32(image, flagScanAt + 3, flagManSlot - (flagScanAt + 11));
        FieldAreaPattern.CopyTo(image, areaScanAt);
        Put32(image, areaScanAt + 3, fieldAreaSlot - (areaScanAt + 7));

        Put64(image, fieldAreaSlot, Base + fieldArea);
        Put64(image, fieldArea + 0x10, Base + worldInfoOwner);
        Put32(image, worldInfoOwner + 0x8, 2);                       // two world infos
        Put64(image, worldInfoOwner + 0x10, Base + worldInfos);
        image[worldInfos + 0 * 0x38 + 0xb] = 40;                     // some other area first
        image[worldInfos + 1 * 0x38 + 0xb] = 30;                     // area 30
        image[worldInfos + 1 * 0x38 + 0x20] = 2;                     // with two blocks
        Put64(image, worldInfos + 1 * 0x38 + 0x28, Base + blocks);
        Put32(image, blocks + 0 * 0x70 + 0x8, 0x1E010000);           // m30_01
        Put32(image, blocks + 0 * 0x70 + 0x20, 9);
        Put32(image, blocks + 1 * 0x70 + 0x8, 0x1E000000);           // m30_00
        Put32(image, blocks + 1 * 0x70 + 0x20, 4);                   // category 4 → slot 5

        Put64(image, flagManSlot, Base + flagMan);
        Put64(image, flagMan + 0x218, Base + groups);
        Put64(image, groups + 0 * 0x18, Base + group0);
        Put64(image, groups + 1 * 0x18, Base + group1);
        Put64(image, group1 + 5 * 0xa8 + (0 << 4), Base + AreaFlagWords);
        Put64(image, group0 + 0 * 0xa8 + (6 << 4), Base + GlobalFlagWords);
    }

    /// <summary>Sets or clears one flag bit the way the game stores it: 32 flags per word, highest bit first.</summary>
    private static void SetFlagBit(byte[] image, int wordsAt, int flagWithinThousand, bool on)
    {
        int offset = wordsAt + (flagWithinThousand >> 5) * 4;
        uint mask = 1u << (31 - (flagWithinThousand & 31));
        uint value = BitConverter.ToUInt32(image, offset);
        BitConverter.TryWriteBytes(image.AsSpan(offset, 4), on ? value | mask : value & ~mask);
    }

    private static byte[] Bytes(string hex) => hex.Split(' ').Select(h => Convert.ToByte(h, 16)).ToArray();

    private static void Put64(byte[] image, int offset, long value) => BitConverter.TryWriteBytes(image.AsSpan(offset, 8), value);
    private static void Put32(byte[] image, int offset, int value) => BitConverter.TryWriteBytes(image.AsSpan(offset, 4), value);
    private static void PutFloat(byte[] image, int offset, float value) => BitConverter.TryWriteBytes(image.AsSpan(offset, 4), value);

    /// <summary>
    /// Lays out: code with the scan pattern at 0x1000 whose RIP-relative operand points at a static
    /// slot (0x8000) → WorldChrMan (0x9000) → [+0x80] and [+0x40] PlayerIns (0xA000) → [+0x28]
    /// physics module (0xB000) → position at +0x80.
    /// </summary>
    private static byte[] BuildImage(Vector3 position, bool playerLoaded, bool loading = false)
    {
        var image = new byte[0x10000];
        new Random(1).NextBytes(image.AsSpan(0, 0x800));   // noise before the code

        const int patternAt = 0x1000, staticSlot = 0x8000, worldChrMan = 0x9000, playerIns = 0xA000, physics = 0xB000;
        PlayerPattern.CopyTo(image, patternAt);
        Put32(image, patternAt + 3, staticSlot - (patternAt + 7));          // mov rcx, [rip+rel32]
        Put64(image, staticSlot, Base + worldChrMan);
        if (playerLoaded)
        {
            Put64(image, worldChrMan + 0x80, Base + playerIns);
            Put64(image, worldChrMan + 0x40, Base + playerIns);
            Put64(image, playerIns + 0x28, Base + physics);
            PutFloat(image, physics + 0x80, position.X);
            PutFloat(image, physics + 0x84, position.Y);
            PutFloat(image, physics + 0x88, position.Z);
        }

        // "Loading": mov byte ptr [rip+rel32], imm8 — the flag is read from the byte before the target.
        const int loadingAt = 0x2000, loadingTarget = 0x8101;
        LoadingPattern.CopyTo(image, loadingAt);
        Put32(image, loadingAt + 2, loadingTarget - (loadingAt + 7));
        image[loadingTarget - 1] = (byte)(loading ? 1 : 0);
        if (playerLoaded)
            Put32(image, playerIns + 0x1FE0, 0x1E000000);                  // the map the player is in: m30_00_00_00
        AddEventFlagStructures(image);
        return image;
    }

    [TestMethod]
    public void ResolvesPointers_AndReadsThePlantedPosition()
    {
        var expected = new Vector3(-19.244f, 41.207f, -14.783f);
        byte[] image = BuildImage(expected, playerLoaded: true);
        var memory = new FakeMemory(Base, image);
        var ds3 = new DarkSouls3();

        var failed = ds3.InitPointers(memory, Base, image);

        Assert.AreEqual(0, failed.Count, "scans failed: " + string.Join(",", failed));
        Assert.IsTrue(ds3.CanReadPlayer);
        Assert.AreEqual(Base + 0x8000, ds3.WorldChrManStaticAddress);
        Assert.IsTrue(ds3.IsPlayerLoaded());
        Assert.IsFalse(ds3.IsLoading());
        Assert.IsTrue(ds3.TryGetPosition(out var position));
        Assert.AreEqual(expected, position);
        Assert.AreEqual(expected, ds3.GetPosition());
    }

    [TestMethod]
    public void ReadsTheMapId_OnlyWhileACharacterIsLoaded()
    {
        byte[] image = BuildImage(new Vector3(1, 2, 3), playerLoaded: true);
        var ds3 = new DarkSouls3();
        ds3.InitPointers(new FakeMemory(Base, image), Base, image);
        Assert.IsTrue(ds3.TryGetMapId(out uint map));
        Assert.AreEqual(0x1E000000u, map);

        byte[] empty = BuildImage(default, playerLoaded: false);
        var noCharacter = new DarkSouls3();
        noCharacter.InitPointers(new FakeMemory(Base, empty), Base, empty);
        Assert.IsFalse(noCharacter.TryGetMapId(out _));
    }

    [TestMethod]
    public void ReadsEventFlags_AndSeesThemFlip()
    {
        byte[] image = BuildImage(new Vector3(1, 2, 3), playerLoaded: true);
        var ds3 = new DarkSouls3();
        Assert.AreEqual(0, ds3.InitPointers(new FakeMemory(Base, image), Base, image).Count);
        Assert.IsTrue(ds3.CanReadEventFlags);

        // An area flag (area 30, block 0): found through the world block info of m30_00.
        Assert.IsFalse(ds3.ReadEventFlag(13000009));
        SetFlagBit(image, AreaFlagWords, 9, on: true);
        Assert.IsTrue(ds3.ReadEventFlag(13000009));
        Assert.IsFalse(ds3.ReadEventFlag(13000008), "the neighbouring flag is a different bit");
        Assert.IsFalse(ds3.ReadEventFlag(13000010));

        // Flags are packed 32 to a word: 13000040 is bit 8 of the second word.
        SetFlagBit(image, AreaFlagWords, 40, on: true);
        Assert.IsTrue(ds3.ReadEventFlag(13000040));
        Assert.AreEqual(1u << 23, BitConverter.ToUInt32(image, AreaFlagWords + 4));

        // A flag without an area (category 0).
        Assert.IsFalse(ds3.ReadEventFlag(6500));
        SetFlagBit(image, GlobalFlagWords, 500, on: true);
        Assert.IsTrue(ds3.ReadEventFlag(6500));

        // Clearing flips it back.
        SetFlagBit(image, AreaFlagWords, 9, on: false);
        Assert.IsFalse(ds3.ReadEventFlag(13000009));

        // An area that is not loaded reads false instead of throwing or reading a neighbour.
        Assert.IsFalse(ds3.ReadEventFlag(13100009), "block 1 of area 30 has a different category slot with no words");
        Assert.IsFalse(ds3.ReadEventFlag(15000009), "area 50 is not in the world info list");
    }

    [TestMethod]
    public void NoCharacter_IsReportedInsteadOfAZeroPosition()
    {
        byte[] image = BuildImage(default, playerLoaded: false, loading: true);
        var ds3 = new DarkSouls3();
        ds3.InitPointers(new FakeMemory(Base, image), Base, image);

        Assert.IsTrue(ds3.CanReadPlayer, "the scan itself still resolves on the title screen");
        Assert.IsFalse(ds3.IsPlayerLoaded());
        Assert.IsTrue(ds3.IsLoading());
        Assert.IsFalse(ds3.TryGetPosition(out _), "a broken pointer chain must not yield (0,0,0) as a position");
    }

    [TestMethod]
    public void UnknownBuild_FailsTheScanCleanly()
    {
        var image = new byte[0x4000];   // no pattern anywhere
        var ds3 = new DarkSouls3();
        var failed = ds3.InitPointers(new FakeMemory(Base, image), Base, image);

        CollectionAssert.Contains(failed, DarkSouls3.PlayerInsScan);
        Assert.IsFalse(ds3.CanReadPlayer);
        Assert.IsFalse(ds3.IsPlayerLoaded());
        Assert.IsFalse(ds3.TryGetPosition(out _));
    }

    [TestMethod]
    public void UnreadableMemory_NeverThrows()
    {
        byte[] image = BuildImage(new Vector3(1, 2, 3), playerLoaded: true);
        var ds3 = new DarkSouls3();
        ds3.InitPointers(new FakeMemory(Base, image), Base, image);

        // Point WorldChrMan outside the readable block: the game is tearing the character down.
        Put64(image, 0x8000, 0x7FFF_0000_0000);
        Assert.IsFalse(ds3.IsPlayerLoaded());
        Assert.IsFalse(ds3.TryGetPosition(out _));
    }

    [TestMethod]
    public void PatternSearch_HonoursWildcards_AndCountsMatches()
    {
        byte[] haystack = Bytes("00 48 8b 0d aa bb cc dd 45 00 48 8b 0d 11 22 33 44 45 48 8b 0d 01 02 03 04 46");
        var pattern = MemoryScanner.ToBytePattern("48 8b 0d ? ? ? ? 45");

        Assert.IsTrue(MemoryScanner.TryFind(haystack, pattern, out long first));
        Assert.AreEqual(1, first);
        Assert.AreEqual(2, MemoryScanner.Count(haystack, pattern));   // the third candidate ends in 46

        Assert.IsFalse(MemoryScanner.TryFind(haystack, MemoryScanner.ToBytePattern("48 8b 0d ? ? ? ? 47"), out _));
        // A pattern that would run past the end is not a match.
        Assert.IsFalse(MemoryScanner.TryFind(Bytes("48 8b 0d 01 02"), pattern, out _));
    }

    [TestMethod]
    public void UpstreamPatterns_AreUniqueInTheSyntheticImage()
    {
        byte[] image = BuildImage(default, playerLoaded: true);
        foreach (var node in new DarkSouls3().GetTreeBuilder().Tree)
            Assert.AreEqual(1, MemoryScanner.Count(image, MemoryScanner.ToBytePattern(node.Pattern)), node.Name);
    }
}
