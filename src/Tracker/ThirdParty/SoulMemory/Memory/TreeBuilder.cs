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
// Local modification (see PROVENANCE.md): upstream NodeType.cs, PointerNode.cs,
// PointerAppender.cs and TreeBuilder.cs merged into this one file, unchanged in behaviour.

using System.Collections.Generic;

namespace SoulMemory.Memory;

public enum NodeType
{
    RelativeScan,
    AbsoluteScan,
    Pointer
}

public class PointerNode
{
    public NodeType NodeType;

    //Used for scans
    public string Name = "";
    public string Pattern = "";

    //Used for relative scans
    public long AddressOffset;
    public long InstructionSize;

    //used for absolute scans
    public long? Offset;

    //used for pointers
    public long[] Offsets = [];
    public Pointer Pointer = new();

    public List<PointerNode> Pointers = [];

    public override string ToString() => Name;
}

public class PointerAppender
{
    private readonly PointerNode _pointerNode;
    internal PointerAppender(PointerNode pointerNode)
    {
        _pointerNode = pointerNode;
    }

    public PointerAppender AddPointer(Pointer pointer, params long[] offsets)
    {
        var node = new PointerNode
        {
            NodeType = NodeType.Pointer,
            Name = _pointerNode.Name,
            Pattern = _pointerNode.Pattern,
            Offsets = offsets,
            Pointer = pointer
        };
        _pointerNode.Pointers.Add(node);
        return this;
    }
}

/// <summary>
/// A TreeBuilder lets you define a data tree, from scans and offsets, that end in pointers.
/// The tree doesn't do anything on it's own, but you can pass it to the MemoryScanner to resolve the pointers,
/// or to validate the patterns.
/// </summary>
public class TreeBuilder
{
    public List<PointerNode> Tree = [];

    public PointerAppender ScanRelative(string name, string pattern, long addressOffset, long instructionSize)
    {
        var node = new PointerNode
        {
            NodeType = NodeType.RelativeScan,
            Name = name,
            Pattern = pattern,
            AddressOffset = addressOffset,
            InstructionSize = instructionSize
        };
        Tree.Add(node);
        return new PointerAppender(node);
    }
}
