using System.IO.Compression;
using MapBuilder.Crypto;
using MapBuilder.Render;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SoulsFormats.Cryptography;

namespace MapBuilder.Tests;

[TestClass]
public class FormatTests
{
    [TestMethod]
    public void PathHash_MatchesFromSoftAlgorithm()
    {
        // Independent reimplementation of the 37-based hash for cross-checking.
        static uint Reference(string s)
        {
            s = s.ToLowerInvariant().Replace('\\', '/');
            if (!s.StartsWith('/')) s = "/" + s;
            uint h = 0;
            foreach (char c in s) h = h * 37u + c;
            return h;
        }

        foreach (var p in new[]
        {
            "/map/mapstudio/m30_00_00_00.msb.dcx",
            "/map/m30_00_00_00/m30_00_00_00.nva.dcx",
            @"map\m30_00_00_00\m30_00_00_00.nvmhktbnd.dcx",
        })
        {
            Assert.AreEqual(Reference(p), HashHelper.FromPathHash(p), p);
        }
    }

    [TestMethod]
    public void Pkcs1Pem_Parses_2048BitModulus()
    {
        foreach (var (name, pem) in ArchiveKeys.DarkSouls3)
        {
            var (n, e) = BhdCrypto.ParsePkcs1PublicKey(pem);
            Assert.AreEqual(256, n.GetByteCount(isUnsigned: true), $"{name}: modulus should be 2048-bit");
            Assert.IsTrue(e > 0, $"{name}: exponent must be positive");
        }
    }

    [TestMethod]
    public void Crc32_MatchesKnownVector()
    {
        // Standard CRC-32 check value for "123456789".
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual(0xCBF43926u, PngWriter.Crc32(data));
    }

    [TestMethod]
    public void PngWriter_ProducesValidStructure()
    {
        var img = new Rgba32Image(3, 2);
        img.Fill(10, 20, 30, 255);
        img.BlendPixel(1, 1, 200, 100, 50, 255);
        string path = Path.Combine(Path.GetTempPath(), $"mapbuilder_test_{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.Write(path, img);
            byte[] png = File.ReadAllBytes(path);

            // Signature.
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            // IHDR with correct dimensions.
            Assert.AreEqual("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
            Assert.AreEqual(3, ReadBE(png, 16));
            Assert.AreEqual(2, ReadBE(png, 20));

            // Decode the IDAT payload and verify the modified pixel survives a zlib round-trip.
            int idatStart = FindChunk(png, "IDAT");
            int idatLen = ReadBE(png, idatStart - 4);
            using var ms = new MemoryStream(png, idatStart + 4, idatLen);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            byte[] raw = outMs.ToArray();
            Assert.AreEqual((3 * 4 + 1) * 2, raw.Length);
            // Row 1, pixel 1 => offset: row start (13) + filter byte (1) + pixel (4)
            Assert.AreEqual(200, raw[13 + 1 + 4]);
            Assert.AreEqual(100, raw[13 + 1 + 5]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static int ReadBE(byte[] b, int off) => (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

    private static int FindChunk(byte[] png, string type)
    {
        for (int i = 8; i < png.Length - 4; i++)
            if (png[i] == type[0] && png[i + 1] == type[1] && png[i + 2] == type[2] && png[i + 3] == type[3])
                return i;
        throw new AssertFailedException($"chunk {type} not found");
    }
}
