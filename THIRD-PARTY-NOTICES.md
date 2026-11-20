# Third-party notices

Loot Goblins Map is licensed under the GNU General Public License, version 3 (see `LICENSE`).
It contains or ships the following work by others. No part of Dark Souls III is included: the
maps are built on your computer from your own installed copy of the game.

## Source code included in the program

| Work | Upstream, pinned commit | License | Where it is used |
|---|---|---|---|
| SoulsFormatsNEXT | https://github.com/soulsmods/SoulsFormatsNEXT `e25d1a5c3cda83b801fdefe00f4d2903892cc526` | GPL-3.0 | Reading the game's archive, map, navigation and text file formats (`src/MapBuilder/ThirdParty/SoulsFormats`) |
| Smithbox (its SoulsFormats fork, HKX reader) | https://github.com/vawser/Smithbox `2145776b19446e78692a8caca87beaeb5788e4eb` | GPL-3.0 | Reading the Havok navigation mesh (`src/MapBuilder/ThirdParty/SoulsFormats/Formats/HKX`) |
| UXM (archive keys) | https://github.com/JKAnderson/UXM `7da971b7e9a817b447335b5ebe9d845b1f98ad0d` | GPL-3.0 | The public keys of the game's archive headers (`src/MapBuilder/Crypto/ArchiveKeys.cs`) |
| SoulSplitter (SoulMemory, Dark Souls III subset) | https://github.com/FrankvdStam/SoulSplitter `aeb7cbee11ffd3c0bcf3bf5eac4a861b267b5db4` | GPL-3.0 | Locating the player position and event flags in the running game, read-only (`src/Tracker/ThirdParty/SoulMemory`) |

The full license texts are next to the vendored code (`LICENSE-SoulsFormatsNEXT.txt`,
`LICENSE-Smithbox.txt`, `LICENSE-SoulSplitter.txt`); all three are the GPL version 3, the same
text as `LICENSE`. What was taken, what was changed and what was deliberately left out is
recorded in the `PROVENANCE.md` file in each folder.

## Facts used, nothing copied

| Source | Pinned commit | What was used |
|---|---|---|
| Paramdex | https://github.com/soulsmods/Paramdex `ff7245e524329bc3eab00036723d2bd53384cedf` | Field offsets in five of the game's parameter tables. The repository has no license file, so none of its files are included. |
| The Grand Archives cheat table | https://github.com/The-Grand-Archives/Dark-Souls-III-CT-TGA `6f6a1e0f726f313b43814c6fa939cf60f9d8b05b` | One offset (where the current map id is kept). The repository has no license file, so nothing was copied from it. |

## Font

| Work | Upstream, pinned commit | License |
|---|---|---|
| EB Garamond (Medium) | https://github.com/octaviopardo/EBGaramond12 `106a4a6d377987459ae5e68673a4570f13b957fb`, file `fonts/ttf/EBGaramond-Medium.ttf`, unmodified | SIL Open Font License 1.1 (`Fonts/OFL.txt`). Copyright 2017 The EB Garamond Project Authors. |

## Runtime shipped inside the released executable

The released `LootGoblinsMap.exe` is self-contained: it carries Microsoft's .NET runtime and
the Windows Desktop (WPF) runtime so that nothing has to be installed.

| Work | Source | License |
|---|---|---|
| .NET runtime | https://github.com/dotnet/runtime | MIT, with the third-party notices at https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT |
| Windows Presentation Foundation | https://github.com/dotnet/wpf | MIT |

The automated tests use Microsoft's test packages (`Microsoft.NET.Test.Sdk`, `MSTest.TestAdapter`,
`MSTest.TestFramework`, MIT); they are not part of the released program.

## Not affiliated

Loot Goblins Map is an unofficial fan project. It is not affiliated with, endorsed by or
connected to FromSoftware, Inc. or Bandai Namco Entertainment. Dark Souls is a trademark of
its owners. The program contains no game files, text, images, icons or fonts from the game.
