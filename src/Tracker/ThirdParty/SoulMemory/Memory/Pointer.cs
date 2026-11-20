// This file is part of the SoulSplitter distribution (https://github.com/FrankvdStam/SoulSplitter).
// Copyright (c) 2022 Frank van der Stam.
// https://github.com/FrankvdStam/SoulSplitter/blob/main/LICENSE
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
//
// Local modification (see PROVENANCE.md): reads go through IMemorySource instead of a
// System.Diagnostics.Process handle, every member that changed the target process was
// removed, and only the read helpers the tracker uses were kept (ReadInt32, ReadUInt32,
// ReadInt64, ReadByte, ReadFloat, Append, Copy, CreatePointerFromAddress).

using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulMemory.Memory;

public class Pointer
{
    public IMemorySource? Memory;
    public long BaseAddress;
    public List<long> Offsets = [];
    public bool Is64Bit;
    private bool _initialized;

    public void Initialize(IMemorySource memory, bool is64Bit, long baseAddress, params long[] offsets)
    {
        Memory = memory;
        Is64Bit = is64Bit;
        BaseAddress = baseAddress;
        Offsets = offsets.ToList();
        _initialized = true;
    }

    public void Clear()
    {
        _initialized = false;
        Memory = null;
        BaseAddress = 0;
        Offsets.Clear();
    }

    public bool IsInitialized => _initialized;

    public Pointer Copy()
    {
        var p = new Pointer();
        p.Initialize(Memory!, Is64Bit, BaseAddress, Offsets.ToArray());
        return p;
    }

    public Pointer CreatePointerFromAddress(long? offset = null)
    {
        var copy = Copy();
        var offsets = Offsets.ToList();
        if (offset.HasValue)
        {
            offsets.Add(offset.Value);
        }

        offsets.Add(0);

        copy.BaseAddress = ResolveOffsets(offsets);
        copy.Offsets.Clear();
        return copy;
    }

    public Pointer Append(params long[] offsets)
    {
        var copy = Copy();
        copy.Offsets.AddRange(offsets);
        return copy;
    }

    private long ResolveOffsets(List<long> offsets)
    {
        if (Memory == null)
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[8];
        var ptr = BaseAddress;
        for (var i = 0; i < offsets.Count; i++)
        {
            var offset = offsets[i];
            var address = ptr + offset;

            if (i + 1 < offsets.Count)
            {
                if (Is64Bit)
                {
                    if (!Memory.TryRead(address, buffer))
                    {
                        return 0;
                    }
                    ptr = BitConverter.ToInt64(buffer);
                }
                else
                {
                    if (!Memory.TryRead(address, buffer[..4]))
                    {
                        return 0;
                    }
                    ptr = BitConverter.ToInt32(buffer);
                }

                if (ptr == 0)
                {
                    return 0;
                }
            }
            else
            {
                ptr = address;
            }
        }

        return ptr;
    }

    /// <summary>Local modification: reports whether the bytes could actually be read.</summary>
    public bool TryReadBytes(Span<byte> destination, long? offset = null)
    {
        destination.Clear();
        if (!_initialized || Memory == null)
        {
            return false;
        }

        var offsetsCopy = Offsets.ToList();
        if (offset.HasValue)
        {
            offsetsCopy.Add(offset.Value);
        }

        var address = ResolveOffsets(offsetsCopy);
        return address != 0 && Memory.TryRead(address, destination);
    }

    public bool IsNullPtr()
    {
        return GetAddress() == 0;
    }

    public long GetAddress()
    {
        return ResolveOffsets(Offsets);
    }

    public int ReadInt32(long? offset = null)
    {
        Span<byte> b = stackalloc byte[4];
        TryReadBytes(b, offset);
        return BitConverter.ToInt32(b);
    }

    public uint ReadUInt32(long? offset = null)
    {
        Span<byte> b = stackalloc byte[4];
        TryReadBytes(b, offset);
        return BitConverter.ToUInt32(b);
    }

    public byte ReadByte(long? offset = null)
    {
        Span<byte> b = stackalloc byte[1];
        TryReadBytes(b, offset);
        return b[0];
    }

    public long ReadInt64(long? offset = null)
    {
        Span<byte> b = stackalloc byte[8];
        TryReadBytes(b, offset);
        return BitConverter.ToInt64(b);
    }

    public float ReadFloat(long? offset = null)
    {
        Span<byte> b = stackalloc byte[4];
        TryReadBytes(b, offset);
        return BitConverter.ToSingle(b);
    }
}
