using System.Buffers.Binary;
using System.Text;

namespace MapBuilder.GameData;

/// <summary>One row of a param: an id and a window onto its bytes. Fields are read by offset.</summary>
public readonly struct ParamRow
{
    public int Id { get; }
    private readonly byte[] _file;
    private readonly int _offset;
    private readonly int _size;

    public ParamRow(int id, byte[] file, int offset, int size)
    {
        Id = id;
        _file = file;
        _offset = offset;
        _size = size;
    }

    private ReadOnlySpan<byte> At(int fieldOffset, int length)
    {
        if (fieldOffset < 0 || fieldOffset + length > _size)
            throw new InvalidDataException($"Field at 0x{fieldOffset:X} (+{length}) lies outside the {_size}-byte row {Id}.");
        return _file.AsSpan(_offset + fieldOffset, length);
    }

    public int S32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(At(offset, 4));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(At(offset, 4));
    public short S16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(At(offset, 2));
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(At(offset, 2));
    public byte U8(int offset) => At(offset, 1)[0];
    public sbyte S8(int offset) => (sbyte)At(offset, 1)[0];
    public float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(At(offset, 4));
}

/// <summary>
/// Minimal reader for FromSoftware PARAM files as Dark Souls III stores them (little-endian,
/// 64-bit row offsets, or the older 32-bit form). It reads the row table; field layouts are the
/// caller's business (see <see cref="ParamLayouts"/>). Format reference: SoulsFormats PARAM.cs.
/// </summary>
public sealed class ParamFile
{
    public string ParamType { get; private init; }
    /// <summary>Size of one row's data in bytes, taken from the spacing of the rows.</summary>
    public int RowSize { get; private init; }

    private byte[] _bytes;
    private readonly List<(int Id, int Offset)> _rows = new();
    private readonly Dictionary<int, int> _byId = new();

    public int RowCount => _rows.Count;

    public IEnumerable<ParamRow> Rows => _rows.Select(r => new ParamRow(r.Id, _bytes, r.Offset, RowSize));

    public bool TryGetRow(int id, out ParamRow row)
    {
        if (_byId.TryGetValue(id, out int offset))
        {
            row = new ParamRow(id, _bytes, offset, RowSize);
            return true;
        }
        row = default;
        return false;
    }

    public bool Contains(int id) => _byId.ContainsKey(id);

    public static ParamFile Read(byte[] bytes)
    {
        if (bytes.Length < 0x40)
            throw new InvalidDataException("Param file is too short.");
        if (bytes[0x2C] != 0)
            throw new InvalidDataException("Big-endian param files are not supported.");
        byte format = bytes[0x2D];
        bool longOffsets = (format & 0x04) != 0;
        bool intDataOffset = (format & 0x01) != 0 && (format & 0x02) != 0;

        int rowCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x0A));
        string type;
        if ((format & 0x80) != 0)
        {
            // The param type is a string elsewhere in the file; its offset is at 0x10.
            long typeOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0x10));
            int end = typeOffset > 0 && typeOffset < bytes.Length ? Array.IndexOf(bytes, (byte)0, (int)typeOffset) : -1;
            type = end > 0 ? Encoding.ASCII.GetString(bytes, (int)typeOffset, end - (int)typeOffset) : "";
        }
        else
        {
            type = Encoding.ASCII.GetString(bytes, 0x0C, 0x20);
            int nul = type.IndexOf('\0');
            if (nul >= 0)
                type = type[..nul];
        }

        long stringsOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int rowsStart = longOffsets || intDataOffset ? 0x40 : 0x30;
        int rowHeader = longOffsets ? 24 : 12;
        if (rowsStart + (long)rowCount * rowHeader > bytes.Length)
            throw new InvalidDataException($"Param '{type}': row table runs past the end of the file.");

        var file = new ParamFile { ParamType = type, _bytes = bytes };
        var offsets = new List<long>(rowCount);
        for (int i = 0; i < rowCount; i++)
        {
            int at = rowsStart + i * rowHeader;
            int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            long dataOffset = longOffsets
                ? BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 8))
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (dataOffset <= 0 || dataOffset >= bytes.Length)
                throw new InvalidDataException($"Param '{type}': row {id} has a data offset outside the file.");
            offsets.Add(dataOffset);
            file._rows.Add((id, (int)dataOffset));
            file._byId.TryAdd(id, (int)dataOffset);   // the game uses the first row of a duplicated id
        }

        // Row size: rows are stored back to back, so it is the smallest gap between two rows; a
        // single row runs up to the strings (or the end of the file).
        long size = long.MaxValue;
        var sorted = offsets.Distinct().OrderBy(o => o).ToList();
        for (int i = 1; i < sorted.Count; i++)
            size = Math.Min(size, sorted[i] - sorted[i - 1]);
        if (sorted.Count == 1)
        {
            long end = stringsOffset > sorted[0] && stringsOffset <= bytes.Length ? stringsOffset : bytes.Length;
            size = end - sorted[0];
        }
        if (sorted.Count > 0 && sorted[^1] + size > bytes.Length)
            throw new InvalidDataException($"Param '{type}': the last row runs past the end of the file.");

        return new ParamFile
        {
            ParamType = type,
            RowSize = sorted.Count == 0 ? 0 : (int)size,
            _bytes = bytes,
        }.CopyRowsFrom(file);
    }

    private ParamFile CopyRowsFrom(ParamFile other)
    {
        _rows.AddRange(other._rows);
        foreach (var kv in other._byId)
            _byId[kv.Key] = kv.Value;
        return this;
    }
}
