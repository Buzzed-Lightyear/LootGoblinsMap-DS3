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

---

# Phase 2 — live tracker MVP (High Wall, Windows)

Date: 2026-11-20. Built and run on native Windows 11, .NET SDK 10.0.404 (in `deps/dotnet`).

> Later the same day the owner ran the in-game test and the layering was changed. Where this
> section says "not read the running game", "unverified" or "layers are stack order", read the
> follow-up at the end of this file first.

## Result in one paragraph

The tracker is built and works end to end on recorded positions: map, markers, dot, trail,
layer highlight, orientation toggles, replay and PNG export. It has **not** read the running
game yet. The game was running during this session, but Steam was started as administrator, so
the game was elevated and Windows refused the tracker's read-only open with "access denied".
Hard rule 4 forbids running the tracker elevated, so the live read stopped there. Because of
that, the **coordinate space is unverified** and the in-game test in `TESTING.md` is still to do.

## What was built

- **`src/MapCore`** — shared library used by both programs: the world↔pixel transform (now with
  90° rotation), top-down geometry, the map.json model, the polygon file, the polygon/layer
  lookup, the per-tick tracking logic (states, trail), the replay format and `Config`.
- **`src/MapBuilder`** — now also writes `out/<map>/navpolys.bin`: every navmesh polygon's
  world-space vertices and layer index (13,672 polygons, 711 KB). Honours `"rotation"` in
  config.json. Layering algorithm untouched; images are the same as before the refactor.
- **`src/Tracker`** — WPF window on `net10.0-windows`: all layers drawn together (current one
  bright, the rest dim), bonfire and treasure markers, player dot, trail; follow, zoom,
  fit-to-window, always-on-top; flip X / flip Z / rotate 90° with a save to config.json; debug
  readout (attach status, game version, world x/y/z, pixel x/y, layer, distance to polygon);
  messages for game not running, no character loaded, outside High Wall and access denied, each
  clearing on its own. Reads 25 times a second on a background thread.
- **Memory reading** — the DS3 patterns and offsets from SoulMemory (SoulSplitter `aeb7cbe`,
  GPLv3), source-vendored and cut down to the player position and the "character loaded" test.
  Upstream does not meet hard rule 2 (it opens the process with all rights and carries
  memory-changing helpers), so those parts were left out and the tracker has its own process
  layer that opens the game with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` only.
- **Replay** — `Tracker --replay <file>` plays a list of positions in the window;
  `--png` renders without a window; `--make-path` writes a synthetic walk over the navmesh
  between two bonfire markers; `--probe` is the live read check as a command.
- `run-tracker.cmd`, `build-map.cmd`, `TESTING.md`, `LICENSE` (GPLv3), `.gitignore`.

## Checks

1. **Game folder snapshot identical before and after — FAIL (by the letter).**
   186 files before and after, none added or removed, but 11 differ in size or modified time:
   `Game/logs/` (7 log files), `Game/dxgi.ini`, `Game/SoulsUnsqueezed.ini`,
   `Game/Version/installed.ini`, `Game/Version/repository.ini`. All eleven belong to the injected
   mods (Special K / SoulsUnsqueezed), which rewrite them whenever the game starts. The game
   process started at 13:39:19, the same second the before-snapshot was written; nine of the
   eleven were last written between 13:39:20 and 13:39:28, `dxgi.log` at 13:40:44 and
   `steam_api.log` at 13:52:29 while the game ran. Nine of them had already changed once between
   two read-only listings taken 40 seconds apart at the very start of the session, before any
   project code had opened a file in the game folder. Every game data file (`Data*.bhd/.bdt`,
   the executable, DLLs) is unchanged. Nothing in this project writes to the game path: the
   builder opens the archives read-only, and the tracker's read-only open of the process was
   refused. As a control, a snapshot taken after the game had closed and another taken after a
   further full map build and all remaining work are identical to each other. The diff is in
   `work/game_diff.txt`. To get a clean result, repeat the two snapshots with the game closed.
2. **`dotnet build` and `dotnet test` from a clean checkout — PASS.** The source tree (tracked
   and untracked files, nothing ignored; no `bin/`, `obj/`, `out/`, `work/`, `deps/`) was copied to
   an empty folder: build 0 errors, 49/49 tests pass (34 in `MapBuilder.Tests`, 15 in
   `Tracker.Tests`). The 3 build warnings are in the phase 1 vendored SoulsFormats code.
3. **Unit tests for lookup and pixel parity — PASS.**
   - Polygon and layer lookup (`CoreTests`, 9 tests): stacked floors pick the smallest vertical
     distance; the layer comes from the polygon, not the height; ramps measure to the sloped
     surface; nearest within 3 m in 3D and nothing beyond; the far-above refinement and its
     fallback; non-finite input; and 2,000 random points on a 400-polygon mesh agreeing with a
     brute-force scan.
   - Builder and tracker pixels (`PixelParityTests`, 3 tests): the builder's real export is run
     on a synthetic map in all 16 flip/rotation combinations and loaded the way the tracker
     loads it. Marker pixels stored by the builder equal the tracker's within 1e-9 px; 500
     random world points per orientation are identical; re-orienting in the tracker equals what
     a rebuild with the saved config produces; standing on a marker puts the dot on the
     marker's pixel and layer.
   - Also covered: polygon file round trip, rotation semantics, config save keeping comments,
     replay parsing, tracking states and trail breaks, and the vendored pointer code against a
     fake process image.
4. **Source scan for banned APIs — PASS.** `ReadOnlyGuaranteeTests` (6 tests):
   - 31 files under `src/Tracker` and `src/MapCore` (code, comments, docs) contain none of 37
     banned names, including the four named in hard rule 2 and their relatives (allocation,
     protection, remote threads, library loading, hooks, debugging, input, focus, process start
     and stop, and the wider access masks).
   - Native imports, in source and in the compiled `Tracker.dll`, are exactly these eight:
     `OpenProcess`, `ReadProcessMemory`, `CloseHandle`, `GetExitCodeProcess`,
     `QueryFullProcessImageNameW` (kernel32), `NtQueryInformationProcess` (ntdll),
     `OpenProcessToken`, `GetTokenInformation` (advapi32). `MapCore.dll` has none.
   - The open call is private and has two call sites, with masks 0x1010 (read + limited query)
     and 0x1000 (limited query only, used to explain an access-denied).
   - Neither assembly references a networking assembly or type (hard rule 5), nor the managed
     `Process` members that start, stop or over-open a process.
5. **Replay mode — PASS.** A 235.5 m synthetic walk over the navmesh between bonfire 3001950
   and bonfire 3001955 (479 positions) was replayed and rendered. All 479 positions tracked,
   477 inside a polygon, largest distance to the located polygon 0.13 m, layers visited 0, 1, 2
   and 3. The PNGs were opened and checked: follow view and whole-map view show the floor plan,
   the current layer bright with the others dim, treasure and bonfire markers, the trail from
   one bonfire circle to the other, and the dot sitting on the destination bonfire. A third
   render with flip X and a 90° turn shows map, markers, dot and trail turned together. The
   real window was also captured in live mode (showing "Dark Souls III is not running") and in
   replay mode.
6. **Live read — NOT DONE.** While the game ran (PID 15896), the tracker's read-only open
   returned access denied. A query-only check showed why: `steam.exe` and `DarkSoulsIII.exe`
   were both elevated (integrity level High) while the tracker ran unelevated, as required.
   No position was read, so "finite and stable" and "within 3 m of a bonfire marker" are
   untested. By the time the tracker was finished the game had been closed; `Tracker --probe`
   then reports "SKIPPED — the game is not running".

## Not verified without the owner

- **That the position reads at all on this game build.** The scan patterns are SoulMemory's and
  were only exercised against a synthetic process image in tests.
- **Coordinate space.** Whether the position from memory is in the same space as the MSB and
  navmesh coordinates. Nothing is assumed or corrected in code; the first look at the tracker
  with the character on a bonfire settles it (`TESTING.md` step 2).
- **Orientation.** Mirroring and rotation against the game (`TESTING.md` step 3).
- **Layer highlight in real movement**, especially stairs and ladders (`TESTING.md` step 4).
- **Detected game version** string, and the "no character loaded" state on the title screen.

## Known issue: layers are stack order, not floors

Unchanged from phase 1, as instructed. A layer is "the lowest surface at this spot, then the
second lowest, and so on", not a connected storey. Height ranges overlap heavily (layer 0 spans
y −90.0…56.1, layer 2 spans −44.6…47.3), so a layer cannot be chosen from the player's height;
the tracker takes it from the navmesh polygon under the player. Visible consequence: walking
along one storey can switch the highlighted layer wherever something lies underneath (the
replayed walk between two bonfires on the same level passes through layers 0, 1, 2 and 3), and
the bright layer is a patchwork rather than one floor.

## Open issues

- Steam runs elevated on this machine. Until it is started normally the tracker cannot attach.
- Check 1 needs a rerun with the game closed for a clean diff.
- "Outside High Wall" is decided geometrically (no polygon within 12 m), not from the game's
  current map id. Another area whose coordinates overlap High Wall's would not be detected.
- The navmesh contains a few stray pieces away from the main map (a large triangle north of it
  and a cluster of small ones to the east, visible in the whole-map view). They are in the
  vanilla data and also widen the image bounds.
- Bonfire markers have entity ids but no names; the data does not say which is which.
- 3 of the 5 real bonfires are on navmesh pieces that share no corners with the first two, so
  the synthetic path generator falls back to a straight line for them.
- The attach copies the game's whole main module once to find the player pointer. Its duration
  on the real game is unmeasured.
- `deps/` is now about 790 MB, almost all of it the .NET SDK. `work/` is 7 MB.
- The git identity fix is local only; see the final message of the session for the push command.

## Vendored code and licenses (addition)

| Source | Commit | License | Used for |
|---|---|---|---|
| FrankvdStam/SoulSplitter (`src/SoulMemory`) | `aeb7cbe` | **GPLv3** | DS3 player-position scan patterns and offsets, pointer resolution (source-vendored subset, read-only) |

Provenance and every local change: `src/Tracker/ThirdParty/SoulMemory/PROVENANCE.md`. The
repository now carries the GPLv3 text in `LICENSE`. NuGet packages: the Microsoft test platform
only, as in phase 1.

---

# Phase 2 follow-up — in-game result and connected-floor layers

Date: 2026-11-20, after the owner restarted Steam without administrator rights.

## In-game result (reported by the owner)

- The tracker attached and tracked; "everything checks out". No orientation change was saved
  (`config.json` still has no flips and no rotation).
- At the High Wall of Lothric bonfire the readout showed world x −18.6, y 41.23, z −14.44. The
  bonfire marker with entity id 3001950 is at (−19.244, 41.207, −14.783): **0.73 m apart**
  (0.64 m in x, 0.02 m in y, 0.34 m in z). The position read from memory is therefore in the
  same space as the MSB and navmesh coordinates, with no offset. This closes the
  "coordinate space" item and identifies 3001950 as the High Wall of Lothric bonfire.
- Finding: the highlighted layer changed in places where the floor only has a tiny step.

Not re-measured by me: the game was closed again by the time of this follow-up, so
`Tracker --probe` still reports "game not running". The numbers above are the owner's reading.

## Why the layer changed over tiny steps

The phase 1 splitter placed every polygon on its own: "the lowest layer where nothing over or
under me is too far away". A storey therefore changed layer wherever something happened to lie
beneath it, even between two polygons sharing an edge. Measured on the High Wall navmesh:
**4,291 seams**, a seam being two polygons that touch in top-down view with less than 0.6 m of
step and sit on different layers; about 3,600 of them had no step at all.

## What changed

New splitter `src/MapBuilder/Pipeline/FloorLayering.cs`, now the default (`"layerMode": "floors"`
in config.json; `"stack"` brings the old behaviour back):

1. **Links.** Polygons whose outlines touch in top-down view (within 0.3 m, or one reaching into
   the other) with a small height step are linked. This also joins separately placed navmesh
   pieces that share no corners.
2. **Level patches.** Polygons no steeper than 15° that are linked with at most 0.6 m of step
   form one patch (a floor, a landing, a deck). A patch is never cut apart.
3. **Regions.** From the lowest unclaimed piece, always taking the lowest reachable piece next,
   a region grows until it would come to lie over itself (more than 1.8 m apart). So the cut
   falls on stairs, not in the middle of a floor.
4. **Layers.** Regions that lie over each other never share a layer; otherwise a region goes
   where the regions it touches are.
5. **Refinement.** Single pieces (a patch as a whole, or one stair polygon) move to the layer
   most of their neighbours are on when nothing there lies over or under them.

The phase 1 guarantee is kept and now checked on every build: no layer lies over itself.

## Result

| | stack order (before) | connected floors (now) |
|---|---|---|
| Layers | 7 | 7 |
| Seams (touching, under 0.6 m step, different layers) | 4,291 | 525 |
| … of them with no step at all (under 5 cm) | 3,631 | 430 |
| … of them between two level polygons | not measured | **0** |
| … of them between a floor and a stair/ramp polygon | not measured | 397 |
| … of them between two stair/ramp polygons | not measured | 128 |
| Layer changes on the 235 m replayed walk between bonfires 3001950 and 3001955 | 17 | 3 |
| Stacked pairs within one layer | 0 | 0 |

The 525 seams that remain are where a floor meets its stairs or part-way up a flight: places
where the layer has to change because the two floors lie over each other.

Checks rerun after the change:

- `dotnet build` / `dotnet test` from a clean copy of the source: 0 errors, 55/55 tests
  (40 in `MapBuilder.Tests`, 15 in `Tracker.Tests`). Six new tests in `LayeringTests.cs`: a
  walkway partly over a room stays one layer; a 20 cm step between separate pieces does not
  change the layer; stairs back over their own floor change layer once, on the stairs; a tilted
  polygon inside a floor goes with that floor; random meshes never produce a layer that lies
  over itself.
- Builder acceptance checks 2–5: pass, unchanged (71/77 markers within 2 m of their layer).
- Replay: rendered and looked at; the bright layer under the player is now one clean floor.
- Read-only scan: unchanged, passes (the tracker was not touched apart from one summary line).
- Game folder snapshot before and after this follow-up (game closed throughout): **identical**,
  186 files, empty diff (`work/game_before_2.txt`, `work/game_after_2.txt`).

## Still open

- Whether the remaining layer changes fall in sensible places in the game (top or bottom of
  stairs, ladders). The 525 are counted, not walked.
- A layer is still a set of floors that do not overlap, not one storey: several unconnected
  floors share each layer and light up together.
- Layer numbers changed with the new splitter; anything that stored the old numbers is stale.
  `out/` was regenerated; restart the tracker to load it.
- The other open issues listed above (geometric "outside High Wall", stray navmesh pieces,
  unnamed bonfires, the unpushed identity fix) are unchanged.
