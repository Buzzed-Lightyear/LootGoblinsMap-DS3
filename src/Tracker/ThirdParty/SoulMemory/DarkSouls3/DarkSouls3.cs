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
// Local modification (see PROVENANCE.md): reduced to what a position tracker needs — the
// "playerIns" and "Loading" scans, IsPlayerLoaded, IsLoading and GetPosition. The patterns
// and offsets are upstream's, byte for byte. Removed: the in-game-time setter (the only
// member that changed the game), attributes, event flags, bosses, bonfires, item pickups
// and the remaining scans. Process handling moved out to the caller, which passes in a
// read-only memory source. The position is returned as System.Numerics.Vector3.

using System;
using System.Collections.Generic;
using System.Numerics;
using SoulMemory.Memory;
using Pointer = SoulMemory.Memory.Pointer;

namespace SoulMemory.DarkSouls3;

public class DarkSouls3
{
    private readonly Pointer _playerIns = new();
    private readonly Pointer _loading = new();
    private readonly Pointer _sprjChrPhysicsModule = new();

    public const string PlayerInsScan = "playerIns";
    public const string LoadingScan = "Loading";

    public TreeBuilder GetTreeBuilder()
    {
        var treeBuilder = new TreeBuilder();

        treeBuilder
            .ScanRelative(PlayerInsScan, "48 8b 0d ? ? ? ? 45 33 c0 48 8d 55 e7 e8 ? ? ? ? 0f 2f 73 70 72 0d f3 ? ? ? ? ? ? ? ? 0f 11 43 70", 3, 7)
                .AddPointer(_playerIns, 0, 0x80)
                .AddPointer(_sprjChrPhysicsModule, 0, 0x40, 0x28);

        treeBuilder
            .ScanRelative(LoadingScan, "c6 05 ? ? ? ? ? e8 ? ? ? ? 84 c0 0f 94 c0 e9", 2, 7)
                .AddPointer(_loading);

        return treeBuilder;
    }

    /// <summary>Resolves the pointers against a copy of the main module. Returns the names of failed scans.</summary>
    public List<string> InitPointers(IMemorySource memory, long baseAddress, byte[] moduleBytes)
    {
        ResetPointers();
        var treeBuilder = GetTreeBuilder();
        return MemoryScanner.ResolvePointers(treeBuilder, memory, baseAddress, moduleBytes, is64Bit: true);
    }

    public void ResetPointers()
    {
        _playerIns.Clear();
        _loading.Clear();
        _sprjChrPhysicsModule.Clear();
    }

    public enum DarkSouls3Version
    {
        Earlier,
        V104,
        V105,
        Later
    };

    public static DarkSouls3Version GetVersion(Version v)
    {
        return v.Minor switch
        {
            0 or 1 or 2 or 3 => DarkSouls3Version.Earlier,
            4 => DarkSouls3Version.V104,
            5 => DarkSouls3Version.V105,
            _ => DarkSouls3Version.Later
        };
    }

    public bool CanReadPlayer => _playerIns.IsInitialized;

    public bool CanReadLoading => _loading.IsInitialized;

    public bool IsLoading()
    {
        return _loading.ReadInt32(-0x1) != 0;
    }

    public bool IsPlayerLoaded()
    {
        return _playerIns.ReadInt64() != 0;
    }

    public Vector3 GetPosition()
    {
        return new Vector3
        (
            _sprjChrPhysicsModule.ReadFloat(0x80),
            _sprjChrPhysicsModule.ReadFloat(0x84),
            _sprjChrPhysicsModule.ReadFloat(0x88)
        );
    }

    /// <summary>Local modification: one read for all three components, reporting failure instead of zeros.</summary>
    public bool TryGetPosition(out Vector3 position)
    {
        Span<byte> b = stackalloc byte[12];
        if (!_sprjChrPhysicsModule.TryReadBytes(b, 0x80))
        {
            position = default;
            return false;
        }
        position = new Vector3(BitConverter.ToSingle(b), BitConverter.ToSingle(b[4..]), BitConverter.ToSingle(b[8..]));
        return true;
    }

    /// <summary>Local modification: addresses for the debug readout.</summary>
    public long PlayerInsAddress => _playerIns.IsInitialized ? _playerIns.ReadInt64() : 0;

    public long WorldChrManStaticAddress => _playerIns.BaseAddress;

    public long PhysicsModuleAddress => _sprjChrPhysicsModule.IsInitialized ? _sprjChrPhysicsModule.GetAddress() : 0;
}
