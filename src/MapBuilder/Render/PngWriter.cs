using System.Buffers.Binary;
using System.IO.Compression;

namespace MapBuilder.Render;

/// <summary>
/// Minimal PNG encoder (8-bit RGBA, non-interlaced), using only the BCL
/// (System.IO.Compression.ZLibStream). Written in-repo because the hard rules forbid
/// prebuilt third-party binaries and DS3 output only needs plain RGBA PNGs.
/// </summary>
public static class PngWriter
{
    public static void Write(string path, Rgba32Image image)
    {
        using var fs = File.Create(path);
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        fs.Write(sig);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(fs, "IHDR", ihdr.ToArray());

        // IDAT: filter byte 0 per scanline, zlib-compressed.
        byte[] raw = new byte[(image.Width * 4 + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int rowStart = y * (image.Width * 4 + 1);
            raw[rowStart] = 0;
            Array.Copy(image.Pixels, y * image.Width * 4, raw, rowStart + 1, image.Width * 4);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        WriteChunk(fs, "IDAT", compressed.ToArray());

        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> crcB = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcB, crc);
        s.Write(crcB);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Crc32(params byte[][] buffers)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var buf in buffers)
            foreach (byte b in buf)
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Simple RGBA raster with polygon fill and line drawing (top-down map rendering).</summary>
public sealed class Rgba32Image
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }  // RGBA rows, top to bottom

    public Rgba32Image(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public void Fill(byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = a;
        }
    }

    public void BlendPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || a == 0)
            return;
        int i = (y * Width + x) * 4;
        if (a == 255)
        {
            Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = 255;
            return;
        }
        int ia = 255 - a;
        Pixels[i] = (byte)((r * a + Pixels[i] * ia) / 255);
        Pixels[i + 1] = (byte)((g * a + Pixels[i + 1] * ia) / 255);
        Pixels[i + 2] = (byte)((b * a + Pixels[i + 2] * ia) / 255);
        Pixels[i + 3] = (byte)Math.Min(255, a + Pixels[i + 3] * ia / 255);
    }

    /// <summary>Scanline fill for a simple polygon given in pixel coordinates.</summary>
    public void FillPolygon(IReadOnlyList<(double X, double Y)> pts, byte r, byte g, byte b, byte a)
    {
        if (pts.Count < 3) return;
        double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(maxY));
        var xs = new List<double>(8);
        for (int y = y0; y <= y1; y++)
        {
            double sy = y + 0.5;
            xs.Clear();
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var (xi, yi) = pts[i];
                var (xj, yj) = pts[j];
                if (yi <= sy && yj > sy || yj <= sy && yi > sy)
                    xs.Add(xi + (sy - yi) / (yj - yi) * (xj - xi));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int xStart = Math.Max(0, (int)Math.Round(xs[k]));
                int xEnd = Math.Min(Width - 1, (int)Math.Round(xs[k + 1]));
                for (int x = xStart; x <= xEnd; x++)
                    BlendPixel(x, y, r, g, b, a);
            }
        }
    }

    public void DrawLine(double x0, double y0, double x1, double y1, byte r, byte g, byte b, byte a)
    {
        double dx = x1 - x0, dy = y1 - y0;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            BlendPixel((int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), r, g, b, a);
        }
    }

    public void FillCircle(double cx, double cy, double radius, byte r, byte g, byte b, byte a)
    {
        int x0 = (int)Math.Floor(cx - radius), x1 = (int)Math.Ceiling(cx + radius);
        int y0 = (int)Math.Floor(cy - radius), y1 = (int)Math.Ceiling(cy + radius);
        double r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
                if (ddx * ddx + ddy * ddy <= r2)
                    BlendPixel(x, y, r, g, b, a);
            }
    }
}
