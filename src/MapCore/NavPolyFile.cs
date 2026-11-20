using System.Numerics;
using System.Text;

namespace MapCore;

/// <summary>A navmesh polygon in world space with the layer the builder gave it.</summary>
public sealed class NavPolygon
{
    public Vector3[] Vertices;
    public int Layer;
    public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

    public NavPolygon(Vector3[] vertices, int layer)
    {
        Vertices = vertices;
        Layer = layer;
        MinX = MinY = MinZ = float.MaxValue;
        MaxX = MaxY = MaxZ = float.MinValue;
        foreach (var v in vertices)
        {
            MinX = Math.Min(MinX, v.X); MaxX = Math.Max(MaxX, v.X);
            MinY = Math.Min(MinY, v.Y); MaxY = Math.Max(MaxY, v.Y);
            MinZ = Math.Min(MinZ, v.Z); MaxZ = Math.Max(MaxZ, v.Z);
        }
    }
}

/// <summary>
/// Compact binary file with every navmesh polygon's world-space vertices and layer index,
/// written by the builder for the tracker (out/&lt;map&gt;/navpolys.bin). Little-endian:
///   "LGNP" | int32 version (1) | int32 polygonCount
///   per polygon: byte layer | byte vertexCount | vertexCount × (float32 x, y, z)
/// </summary>
public static class NavPolyFile
{
    public const string FileName = "navpolys.bin";
    private const string Magic = "LGNP";
    private const int Version = 1;

    public static void Write(string path, IReadOnlyCollection<NavPolygon> polygons)
    {
        using var fs = File.Create(path);
        Write(fs, polygons);
    }

    public static void Write(Stream stream, IReadOnlyCollection<NavPolygon> polygons)
    {
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(Version);
        w.Write(polygons.Count);
        foreach (var p in polygons)
        {
            if (p.Layer is < 0 or > byte.MaxValue)
                throw new InvalidDataException($"Polygon layer {p.Layer} does not fit the file format.");
            if (p.Vertices.Length is < 3 or > byte.MaxValue)
                throw new InvalidDataException($"Polygon with {p.Vertices.Length} vertices does not fit the file format.");
            w.Write((byte)p.Layer);
            w.Write((byte)p.Vertices.Length);
            foreach (var v in p.Vertices)
            {
                w.Write(v.X);
                w.Write(v.Y);
                w.Write(v.Z);
            }
        }
    }

    public static List<NavPolygon> Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static List<NavPolygon> Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
        if (magic != Magic)
            throw new InvalidDataException($"Not a navmesh polygon file (magic '{magic}').");
        int version = r.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported navmesh polygon file version {version}.");
        int count = r.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Negative polygon count.");

        var polygons = new List<NavPolygon>(count);
        for (int i = 0; i < count; i++)
        {
            int layer = r.ReadByte();
            int n = r.ReadByte();
            var verts = new Vector3[n];
            for (int v = 0; v < n; v++)
                verts[v] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            polygons.Add(new NavPolygon(verts, layer));
        }
        return polygons;
    }
}
