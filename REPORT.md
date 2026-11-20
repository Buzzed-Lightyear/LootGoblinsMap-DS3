# REPORT — DS3 map builder MVP, High Wall of Lothric (m30_00_00_00)

Date: 2026-11-20. Built and run on WSL2 (Linux), .NET SDK 10.0.404 (current LTS).

## What was built

A C# command-line tool (`src/MapBuilder`) that, entirely offline and read-only against the
game install:

1. Detects how mods are installed and whether any file needed for High Wall is overridden.
2. Opens the encrypted `Data1–Data5` BHD5/BDT archives **in place** at the game path
   (no archive is copied), RSA-decrypts the `.bhd` headers in memory with the well-known
   DS3 public keys, looks up the three needed files by FromSoft's 37-based path hash,
   reads only those byte ranges from the `.bdt`, AES-decrypts the marked ranges, and
   inflates the zlib (`DFLT`) DCX wrappers.
3. Extracts exactly three files into `work/extract/m30_00_00_00/`:
   `m30_00_00_00.msb` (placements), `m30_00_00_00.nva` (navmesh placement),
   `m30_00_00_00.nvmhktbnd` (BND4 with 94 Havok 2014 `hkaiNavMesh` packfiles).
4. Decodes the navmesh polygons (vendored Smithbox HKX decoder), applies each NVA
   placement transform (scale · Rx · Rz · Ry · translation, radians, matching
   DSMapStudio/Smithbox), producing 13,672 polygons in one map space.
5. Splits polygons into 7 floor layers (bottom-up greedy assignment; a face joins the
   lowest layer where nothing it overlaps in top-down view is more than 1.8 m away
   vertically — ramps stay together, stacked floors split; no layer overlaps itself).
6. Renders each layer as SVG (polygon fill + outline of edges used by only one polygon)
   and rasterizes to PNG with an in-repo scanline rasterizer and minimal PNG encoder
   (BCL only). One shared world→pixel transform for all layers.
7. Reads bonfires (objects with model `o000100`) and treasures (MSB treasure events,
   with item lot IDs) from the MSB, assigns each to a layer by horizontal distance to
   that layer's polygons and vertical offset, and draws them on every output.
8. Writes `out/m30_00_00_00/map.json` with layers (height range, images), the transform
   (origin, px/m, axis convention), and all markers (type, IDs, world position, layer,
   pixel position, distance to layer polygons).

Commands: `mapbuilder scan | extract | build | check | all | snapshot <dir> <out>`,
configuration from `config.json` (`--config`), game path overridable with `--game-path`.

## Map identity

`m30_00_00_00` verified as High Wall of Lothric: the MSB contains the bonfire objects for
the area and treasure names/lots consistent with it; geometry matches the area's known
shape (ramparts chain to a large courtyard, descent to the cathedral, boss arena plaza).

## Acceptance checks

1. **Game path unchanged — PASS.** `work/game_before.txt` vs `work/game_after.txt`
   (relative path, size, mtime for all 186 files): identical, zero diff lines.
2. **Navmesh decodes non-trivially — PASS.** 13,672 polygons; extent 431 m × 644 m,
   height span −90.0 … 56.1 m. Hundreds of metres, not zero, not millions.
3. **Markers near their layer's polygons — PASS.** 71/77 (92.2 %) within 2 m
   horizontally. Outliers, with distance:
   - bonfire `o000100_0007` at (−19.2, **−999.0**, −14.8) — no layer; a disabled/dummy
     bonfire object parked far below the map (vanilla data).
   - treasure `宝死体_0005` at (−157.1, 3.3, 57.0) — no layer within search range.
   - treasure `宝死体_0010` at (−79.7, 29.7, 31.2) — no layer within search range.
   - treasure `宝死体_0039` — 2.27 m; `宝死体_0049` — 2.23 m; `宝死体_0073` — 4.43 m
     (corpses on ledges/railings slightly off the walkable mesh).
4. **At least three bonfires — PASS.** 6 bonfire objects found (5 real + 1 dummy;
   High Wall, Tower on the Wall, Vordt, Dancer, Emma's cathedral are in this map).
5. **World↔pixel round-trip < 1 cm — PASS.** Unit-tested over 1000 random points in all
   four flip configurations (`TransformTests.RoundTrip_WithinOneCentimeter`), plus a
   runtime spot-check across all markers in `check`.
6. **Visual review of every PNG — PASS**, one line per layer:
   - layer 0 (y −90.0…56.1, 10,115 faces): the whole walkable area reads as one
     connected floor plan — ramparts, courtyard, descent, cathedral; clearly High Wall.
   - layer 1 (y −50.5…50.2, 1,900 faces): second storeys above layer-0 floors —
     upper ramparts, keep interiors, tower rooms; coherent clusters, correctly placed.
   - layer 2 (y −44.6…47.3, 1,056 faces): third storeys — rooftop walks and upper rooms
     in the north-west keep area; smaller but contiguous clusters.
   - layer 3 (y −39.9…41.4, 368 faces): fourth level — a few tower floors and roof
     patches; sparse by nature (few places have 4 stacked floors), each patch contiguous.
   - layer 4 (y −27.6…38.1, 147 faces): fifth level — tower tops near the keep and the
     spiral-stair tower by the cathedral; small localized patches.
   - layer 5 (y −4.8…35.0, 82 faces): sixth level — a handful of highest tower
     platforms; tiny but real floors, not noise.
   - layer 6 (y 18.3…33.7, 4 faces): the single topmost platform slivers of the most
     deeply stacked tower; borderline-visible but correct.
7. **`dotnet build` and `dotnet test` from a clean state — PASS.** After deleting all
   `bin/`/`obj/`: build succeeds with 0 errors; 11/11 tests pass. The vendored decoder
   sources live in-repo (`src/MapBuilder/ThirdParty/`), so a clean checkout needs no
   `deps/` clones to build; NuGet restore fetches only Microsoft packages (test platform).

## Mods on this install

Detected (read-only; nothing was modified or removed):

- **SoulsUnsqueezed** (widescreen/aspect fix): `dxgi.dll` (Special K), `SpecialK64.pdb`,
  `SoulsUnsqueezed.ini`, `SUS_Res/`, `CEGUI/`, `Version/`, `logs/` — runtime DLL injection.
- **Honest Merchant** (QoL): `dinput8.dll` + `honestMerchant.ini` — runtime injection.
- **DS3T.exe** (debug tool) — standalone executable.
- **No Mod Engine** (`modengine.ini` absent), **no external mod folder**, **no loose
  unpacked game-data directories** (`map/`, `param/`, … absent).

Therefore **no file needed for High Wall is overridden by any mod; the map was built
from the vanilla archives** (all three files resolved from `Data5`). File-resolution in
the tool still checks mod override → loose file → archive, in that order, so an
overriding install would be picked up and reported.

No DLC archives (`DLC1/DLC2.bdt`) are present on this install; High Wall needs none.

## Vendored code and licenses

| Source | Commit | License | Used for |
|---|---|---|---|
| soulsmods/SoulsFormatsNEXT | `e25d1a5` | **GPLv3** | BHD5, DCX, BND4, MSB3, NVA readers + utilities (source-vendored subset) |
| vawser/Smithbox (`src/Andre/SoulsFormats`) | `2145776` | **GPLv3** | Havok 2014 packfile / `hkaiNavMesh` decoder (`Formats/HKX`) |
| JKAnderson/UXM | `7da971b` | **GPLv3** | DS3 archive RSA public keys (constants); decryption scheme reference |

Full provenance and local modifications: `src/MapBuilder/ThirdParty/SoulsFormats/PROVENANCE.md`.
Consequence: if this repository is ever distributed, it must be under GPLv3-compatible
terms. No prebuilt third-party binaries are used; the only NuGet packages are the
Microsoft test platform (MSTest/Test SDK) for `dotnet test` (see DECISIONS.md).

## Storage

`work/` 5.2 MB + `deps/` 42 MB (shallow/sparse clones, gitignored) + `out/` 3.1 MB —
well under the 500 MB budget. No part of the game was copied; archives were read in place
via `/mnt/g`.

## Axis convention and orientation (known limit)

`pixelX = (worldX − originX) · scale`, `pixelY = (worldZ − originZ) · scale`, world Y is
height. Whether this matches the in-game compass is **unverified** (no game launch
allowed); mirroring is one config switch (`flipX` / `flipZ` in `config.json`), recorded
in `map.json`.

## Windows portability notes (next phase)

The code is plain .NET 10, nothing Linux-only; expected to build and run unchanged.
Specifics to handle when moving:

1. `config.json` → set `gamePath` to `G:\SteamLibrary\steamapps\common\DARK SOULS III`
   (Windows path; the tool accepts the root or the `Game` subfolder).
2. The before/after game listing was produced with Linux `find` this run; on Windows use
   the built-in cross-platform `mapbuilder snapshot <dir> <out>` command instead (same
   content: relative path, size, mtime).
3. NTFS mtime granularity differs from the WSL drvfs view — regenerate the *before*
   snapshot on Windows rather than diffing against the WSL-era file.
4. Paths are case-insensitive on Windows; the loose-file/override probing may therefore
   match files that a case-sensitive Linux filesystem would miss (archives are unaffected
   — hashes are lowercased). This is the game's own behavior, so it is correct there.
5. Console output of Japanese MSB names needs a UTF-8 capable terminal on Windows
   (`chcp 65001`); cosmetic only.
6. `deps/` is only needed to re-audit vendored code; the build itself has no dependency
   on it on either OS.

## Open issues

- Orientation (mirroring/rotation) unverified against the running game — see above.
- 2 treasure events (`宝死体_0026`, `_0033`, item lots 3000260/3000330) have no
  resolvable holding part in the MSB and are skipped (likely cut/event-driven content);
  2 more have a part but sit off any navmesh and get no layer.
- 6 of 94 navmesh models are 1 KB stubs with an `hkaiNavMesh` class but zero polygons
  (vanilla placeholders: n002700, n002702, n003501, n003502, n009000, n900100).
- 20 NVA entries reference `n9xxxxx` connection navmeshes to adjacent maps that are not
  in this map's binder; they are skipped (adjacent maps are out of scope).
- Item names and pickup flag IDs (stretch goal) are not resolved — would need the
  `Data0` regulation (ItemLotParam) and FMG text, deliberately out of this MVP.
- The greedy layering favors a maximal connected layer 0; layers ≥ 3 are sparse tower
  floors. An alternative "strict height band" split would fragment layer 0 instead;
  the chosen trade-off reads best for a loot map.
