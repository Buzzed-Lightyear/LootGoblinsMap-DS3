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
// Local modification (see PROVENANCE.md): reduced to what a loot-map tracker needs — the
// "playerIns", "Loading", "SprjEventFlagMan" and "FieldArea" scans, IsPlayerLoaded, IsLoading,
// GetPosition and ReadEventFlag. The patterns, offsets and the event-flag walk are upstream's.
// Removed: the in-game-time setter (the only member that changed the game), attributes,
// the boss / bonfire / item-pickup tables and the remaining scans. Process handling moved out
// to the caller, which passes in a read-only memory source. The position is returned as
// System.Numerics.Vector3. Added: GetMapId (offset from a public cheat table, see PROVENANCE.md).

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
    private readonly Pointer _sprjEventFlagMan = new();
    private readonly Pointer _fieldArea = new();
    private readonly Pointer _sprjChrPhysicsModule = new();

    public const string PlayerInsScan = "playerIns";
    public const string LoadingScan = "Loading";
    public const string EventFlagScan = "SprjEventFlagMan";
    public const string FieldAreaScan = "FieldArea";

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

        treeBuilder
            .ScanRelative(EventFlagScan, "48 c7 05 ? ? ? ? 00 00 00 00 48 8b 7c 24 38 c7 46 54 ff ff ff ff 48 83 c4 20 5e c3", 3, 11)
                .AddPointer(_sprjEventFlagMan, 0x0);

        treeBuilder
            .ScanRelative(FieldAreaScan, "4c 8b 3d ? ? ? ? 8b 45 87 83 f8 ff 74 69 48 8d 4d 8f 48 89 4d 9f 89 45 8f 48 8d 55 8f 49 8b 4f 10", 3, 7)
                .AddPointer(_fieldArea);

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
        _sprjEventFlagMan.Clear();
        _fieldArea.Clear();
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

    /// <summary>Local modification: whether both scans the event-flag walk needs were resolved.</summary>
    public bool CanReadEventFlags => _sprjEventFlagMan.IsInitialized && _fieldArea.IsInitialized;

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

    /// <summary>
    /// Local addition (not in upstream): the map the player character is in, packed as
    /// area &lt;&lt; 24 | block &lt;&lt; 16 | region &lt;&lt; 8 | index (m30_00_00_00 is 0x1E000000).
    /// The field, PlayerIns + 0x1FE0, is the "Map (m??_??_??_??)" entry of the public
    /// Grand Archives cheat table. Returns false when no character is loaded or the read fails.
    /// </summary>
    public bool TryGetMapId(out uint mapId)
    {
        Span<byte> b = stackalloc byte[4];
        var field = _playerIns.Append(0x1FE0);
        if (!_playerIns.IsInitialized || _playerIns.ReadInt64() == 0 || !field.TryReadBytes(b))
        {
            mapId = 0;
            return false;
        }
        mapId = BitConverter.ToUInt32(b);
        return true;
    }

    /// <summary>Local modification: addresses for the debug readout.</summary>
    public long PlayerInsAddress => _playerIns.IsInitialized ? _playerIns.ReadInt64() : 0;

    public long WorldChrManStaticAddress => _playerIns.BaseAddress;

    public long PhysicsModuleAddress => _sprjChrPhysicsModule.IsInitialized ? _sprjChrPhysicsModule.GetAddress() : 0;

    #region read event flags
    public bool ReadEventFlag(uint eventFlagId)
    {
        // Local modification: nothing to walk before the scans have resolved.
        if (!CanReadEventFlags)
        {
            return false;
        }

        var eventFlagIdDiv10000000 = (int)(eventFlagId / 10000000) % 10;
        var eventFlagArea = (int)(eventFlagId / 100000) % 100;
        var eventFlagIdDiv10000 = (int)(eventFlagId / 10000) % 10;
        var eventFlagIdDiv1000 = (int)(eventFlagId / 1000) % 10;

        //14000002

        //ItemPickup 0x0?
        //Bonfire = 0x11?
        var flagWorldBlockInfoCategory = -1;
        if (eventFlagArea >= 90 || eventFlagArea + eventFlagIdDiv10000 == 0)
        {
            flagWorldBlockInfoCategory = 0;
        }
        else
        {
            //Not implementing a case where Global_FieldArea_Ptr == (FieldArea *)0x0. I think it will just do some initialization there.
            if (_fieldArea.IsNullPtr())
            {
                return false;
            }

            var worldInfoOwner = _fieldArea.Append(0x0, 0x10).CreatePointerFromAddress();

            //Flag stored in world related struct? Looks like the game is reading a size, and then looping over a vector of structs (size 0x38)
            var size = worldInfoOwner.ReadInt32(0x8);
            var vector = worldInfoOwner.Append(0x10);

            // Local modification: a garbage count (struct torn down while loading) must not spin for long.
            if (size < 0 || size > 256)
            {
                return false;
            }

            //Loop over worldInfo structs
            for (var i = 0; i < size; i++)
            {
                //0x00007ff4fd9ba4c3
                var area = vector.ReadByte((i * 0x38) + 0xb);
                if (area == eventFlagArea)
                {
                    //function at 0x14060c650
                    var count = vector.ReadByte(i * 0x38 + 0x20);
                    var index = 0;
                    var found = false;
                    Pointer? worldInfoBlockVector = null;

                    if (count >= 1)
                    {
                        //Loop over worldBlockInfo structs, size 0xe
                        while (true)
                        {
                            worldInfoBlockVector = vector.CreatePointerFromAddress(i * 0x38 + 0x28);
                            var flag = worldInfoBlockVector.ReadInt32((index * 0x70) + 0x8);

                            if ((flag >> 0x10 & 0xff) == eventFlagIdDiv10000 && flag >> 0x18 == eventFlagArea)
                            {
                                found = true;
                                break;
                            }

                            index++;
                            if (count <= index)
                            {
                                found = false;
                                break;
                            }
                        }
                    }

                    if (found)
                    {
                        flagWorldBlockInfoCategory = worldInfoBlockVector!.ReadInt32((index * 0x70) + 0x20);
                        break;
                    }
                }
            }

            if (-1 < flagWorldBlockInfoCategory)
            {
                flagWorldBlockInfoCategory++;
            }
        }


        var ptr = _sprjEventFlagMan.Append(0x218, eventFlagIdDiv10000000 * 0x18, 0x0);

        if (ptr.IsNullPtr() || flagWorldBlockInfoCategory < 0)
        {
            return false;
        }

        var resultPointerAddress = new Pointer();
        resultPointerAddress.Initialize(ptr.Memory!, ptr.Is64Bit, (eventFlagIdDiv1000 << 4) + ptr.GetAddress() + flagWorldBlockInfoCategory * 0xa8, 0x0);

        if (!resultPointerAddress.IsNullPtr())
        {
            var value = resultPointerAddress.ReadUInt32((long)((uint)((int)eventFlagId % 1000) >> 5) * 4);
            var mask = 1 << (0x1f - ((byte)((int)eventFlagId % 1000) & 0x1f) & 0x1f);
            var result = value & mask;
            return result != 0;
        }
        return false;
    }
    #endregion
}
