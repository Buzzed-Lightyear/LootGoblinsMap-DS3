# Vendored third-party source: SoulMemory (DS3 subset)

| Upstream | Commit | License | Upstream path |
|---|---|---|---|
| https://github.com/FrankvdStam/SoulSplitter | `aeb7cbee11ffd3c0bcf3bf5eac4a861b267b5db4` (2025-11-15) | GPLv3 (`LICENSE-SoulSplitter.txt`) | `src/SoulMemory` |

Vendored as source, pinned to that commit, per the hard rule "dependencies come from source in
their official repositories, pinned to a commit". The files keep the `SoulMemory` namespaces
and their GPL headers.

## What was taken

| File here | From upstream | Change |
|---|---|---|
| `DarkSouls3/DarkSouls3.cs` | `DarkSouls3/DarkSouls3.cs` | Reduced to the `playerIns`, `Loading`, `SprjEventFlagMan` and `FieldArea` scans, `IsPlayerLoaded`, `IsLoading`, `GetPosition`, `ReadEventFlag`, `GetVersion`. Patterns, offsets and the event-flag walk are unchanged, apart from one guard against a garbage count. Added locally: `TryGetMapId` (see below). |
| `Memory/Pointer.cs` | `Memory/Pointer.cs` | Reads go through `IMemorySource`; kept: `ReadInt32`, `ReadUInt32`, `ReadInt64`, `ReadByte`, `ReadFloat`, `GetAddress`, `IsNullPtr`, `Append`, `Copy`, `CreatePointerFromAddress`; a failed read resolves to 0 instead of throwing. |
| `Memory/TreeBuilder.cs` | `Memory/NodeType.cs`, `PointerNode.cs`, `PointerAppender.cs`, `TreeBuilder.cs` | Merged into one file; absolute scan dropped. |
| `Memory/MemoryScanner.cs` | `Memory/MemoryScanner.cs` | Only relative-scan pointer resolution kept. The caller passes in the main module's base and bytes. Plain anchored pattern search instead of the upstream Boyer-Moore helper. |
| `Memory/IMemorySource.cs` | — (local) | The one seam to a process: `bool TryRead(long address, Span<byte> buffer)`. It has no way to change anything. |

## Not from SoulMemory: the current-map read

`DarkSouls3.TryGetMapId` reads four bytes at PlayerIns + 0x1FE0. Upstream SoulMemory has no
such read. The offset is the `Map (m??_??_??_??)` entry of the public Grand Archives cheat table
(https://github.com/The-Grand-Archives/Dark-Souls-III-CT-TGA, commit
`6f6a1e0f726f313b43814c6fa939cf60f9d8b05b`). That repository has no license file; only the
offset was used, nothing was copied from it. Verified against game version 1.15.2.0.

## What was deliberately left out, and why

Upstream SoulMemory does not satisfy this project's hard rule 2 as it stands:

- It reads through `System.Diagnostics.Process.Handle`, which opens the game with every access
  right rather than read + limited query.
- `DarkSouls3.cs` has a member that sets the in-game time in the game's memory.
- `Native/` and `MemoryV2/` contain the helpers for changing, allocating and executing memory in
  another process and for loading a library into it; `soulmods/` drives those.

None of those files or members are vendored. Left out entirely: `Native/`, `MemoryV2/`,
`soulmods/`, `Parameters/`, `Memory/BitBlt.cs`, `Memory/Extensions.cs`, `Memory/IMemory.cs`,
every other game's folder, and the DS3 attribute code and the boss, bonfire and item-pickup
tables (tables of game data; this project derives what it needs from the game files at build time).

In their place the tracker has its own small process layer (`src/Tracker/Memory/`), which opens
the game with the read and limited-query rights only and imports nothing that can change a
process. `tests/Tracker.Tests/ReadOnlyGuaranteeTests.cs` scans this directory, the rest of the
tracker and the shared library, in source and in compiled form, to keep it that way.
