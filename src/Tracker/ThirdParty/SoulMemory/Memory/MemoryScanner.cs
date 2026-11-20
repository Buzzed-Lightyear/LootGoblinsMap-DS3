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
// Local modification (see PROVENANCE.md): only pointer resolution by relative scan is kept.
// The caller supplies the main module's base address and a copy of its bytes (read through a
// read-only handle), so nothing here opens or inspects a process. Process discovery, the
// file-based pattern validation and the absolute scan were dropped. The upstream Boyer-Moore
// search lives in a file with unrelated helpers; it is replaced by a plain anchored search.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace SoulMemory.Memory;

public static class MemoryScanner
{
    /// <summary>
    /// Resolve pointers to their correct address by scanning for patterns in a copy of the main module.
    /// Returns the names of the scans that failed (empty when everything resolved).
    /// </summary>
    public static List<string> ResolvePointers(TreeBuilder treeBuilder, IMemorySource memory, long baseAddress, byte[] moduleBytes, bool is64Bit)
    {
        var errors = new List<string>();
        foreach (var node in treeBuilder.Tree)
        {
            if (node.NodeType == NodeType.RelativeScan && TryScanRelative(moduleBytes, baseAddress, node, out var scanResult))
            {
                foreach (var p in node.Pointers)
                {
                    p.Pointer.Initialize(memory, is64Bit, scanResult, p.Offsets);
                }
            }
            else
            {
                errors.Add(node.Name);
            }
        }
        return errors;
    }

    /// <summary>
    /// Scan a previously created buffer of bytes for a given pattern, then interpret the data as AMD64 assembly, where the target address is a relative address
    /// Returns the static address of the given instruction
    /// </summary>
    public static bool TryScanRelative(byte[] bytes, long baseAddress, PointerNode pointerNode, out long result)
    {
        result = 0;
        var pattern = ToBytePattern(pointerNode.Pattern);
        if (TryFind(bytes, pattern, out var scanResult) && scanResult + pointerNode.AddressOffset + 4 <= bytes.Length)
        {
            var address = BitConverter.ToInt32(bytes, (int)(scanResult + pointerNode.AddressOffset));
            result = baseAddress + scanResult + address + pointerNode.InstructionSize;
            return true;
        }
        return false;
    }

    /// <summary>"48 8b 0d ? ? ? ?" → bytes, with null for each wildcard.</summary>
    public static byte?[] ToBytePattern(string pattern)
    {
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new byte?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            result[i] = parts[i] is "?" or "??" ? null : byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return result;
    }

    /// <summary>First match of the pattern at or after <paramref name="start"/>.</summary>
    public static bool TryFind(ReadOnlySpan<byte> bytes, byte?[] pattern, out long index, int start = 0)
    {
        index = -1;

        // Anchor on the leading run of fixed bytes so the bulk of the search is a vectorised IndexOf.
        var anchorLength = 0;
        while (anchorLength < pattern.Length && pattern[anchorLength].HasValue)
        {
            anchorLength++;
        }
        if (anchorLength == 0)
        {
            return false;
        }

        Span<byte> anchor = stackalloc byte[anchorLength];
        for (var i = 0; i < anchorLength; i++)
        {
            anchor[i] = pattern[i]!.Value;
        }

        var position = start;
        while (position + pattern.Length <= bytes.Length)
        {
            var found = bytes[position..].IndexOf(anchor);
            if (found < 0)
            {
                return false;
            }

            position += found;
            if (position + pattern.Length > bytes.Length)
            {
                return false;
            }

            var match = true;
            for (var i = anchorLength; i < pattern.Length; i++)
            {
                if (pattern[i].HasValue && bytes[position + i] != pattern[i]!.Value)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                index = position;
                return true;
            }
            position++;
        }
        return false;
    }

    /// <summary>Number of matches; a healthy pattern matches exactly once.</summary>
    public static int Count(ReadOnlySpan<byte> bytes, byte?[] pattern)
    {
        var count = 0;
        var start = 0;
        while (TryFind(bytes, pattern, out var index, start))
        {
            count++;
            start = (int)index + 1;
        }
        return count;
    }
}
