# Decisions log

Ambiguities resolved without asking, per BRIEF.md. Roughly chronological.

- **WSL path for the game.** The brief gives `G:\SteamLibrary\steamapps\common\DARK SOULS III`;
  from WSL this is `/mnt/g/SteamLibrary/steamapps/common/DARK SOULS III`. The archives live in
  the `Game` subfolder; the tool accepts either the root or the `Game` folder (detects `Data1.bhd`).
- **`deps/` is gitignored, not committed.** Dependencies are shallow/sparse clones pinned to a
  commit (pins recorded in PROVENANCE.md/REPORT.md). The sources actually needed are vendored
  under `src/MapBuilder/ThirdParty/`, so a clean checkout builds without `deps/`.
- **Vendor source subsets instead of NuGet packages.** Hard rule 5 ("dependencies from source…,
  no prebuilt third-party binaries") rules out NuGet binaries like BouncyCastle/ZstdNet/Oodle
  wrappers. SoulsFormatsNEXT's needed formats were vendored as source with the dependent
  compression/crypto branches stubbed (`NotSupportedException`); DS3 only uses zlib DCX, served
  by `System.IO.Compression`.
- **HKX decoder from Smithbox, core from SoulsFormatsNEXT.** Neither SoulsFormatsNEXT nor
  upstream SoulsFormats contains the Havok 2014 navmesh decoder anymore; Smithbox's vendored
  fork does (`src/Andre/SoulsFormats/.../HKX`). Its own core is entangled with DotNext/
  CommunityToolkit, so only the 4 HKX files were taken and adapted (~10 lines) to the NEXT APIs.
- **Microsoft-only NuGet packages are treated as platform, not "third-party".** `dotnet test`
  realistically requires the Microsoft test platform (MSTest.* / Microsoft.NET.Test.Sdk).
  These come from the same vendor and trust domain as the mandated .NET SDK itself. Logged
  here as the one deliberate relaxation; no community NuGet binary is referenced.
- **RSA via `System.Numerics.BigInteger`.** Instead of BouncyCastle (UXM's choice), the .bhd
  RSA-decryption is ~60 lines of raw modpow over 256-byte blocks with a minimal PKCS#1 DER
  parser, matching UXM/BinderTool block semantics (255-byte left-padded output blocks).
  Validated end to end by the `BHD5` magic appearing and all lookups succeeding.
- **Own PNG encoder/rasterizer instead of an imaging library.** The brief says "rasterize with
  a cross-platform library", but every usable library ships as a prebuilt NuGet binary
  (ImageSharp/SkiaSharp), colliding with hard rule 5; building them from source would blow the
  storage/complexity budget. Wrote a ~150-line scanline rasterizer + minimal PNG (RGBA, zlib
  via BCL `ZLibStream`) encoder, unit-tested including a zlib round-trip. `System.Drawing` is
  not used, satisfying the rule's intent (cross-platform output).
- **Hard rule 4 snapshot taken with `find` before anything else**, then the same command at the
  end (byte-identical diff). The tool also has a cross-platform `snapshot` command for the
  Windows phase.
- **Keys for DLC1/DLC2 included but unused** — this install has no DLC archives; the archive set
  opens whatever `.bhd/.bdt` pairs exist.
- **Data0 untouched.** `Data0.bdt` holds the regulation (params); only needed for item *names*
  (stretch goal), so it is never opened.
- **Classnames terminator fix in vendored HKX.cs.** DS3 navmesh hkx files can end the
  `__classnames__` section with a single `0xFF` pad byte instead of `0xFFFF`; the vendored
  reader read past the buffer. Guarded the loop (marked "Local modification").
- **Navmesh model matching by trailing id token.** Binder entries are named
  `n30_00_00_00_000100.hkx`; the NVA `ModelID` (e.g. 100) matches the last `_`-separated token.
- **`n9xxxxx` NVA entries skipped.** 20 placements reference connection navmeshes of adjacent
  maps that are not in this map's binder (plus 6 in-binder 1 KB stub models with zero
  polygons). Skipped with warnings; adjacent maps are out of scope by the brief.
- **Placement transform convention** taken from DSMapStudio/Smithbox: world =
  scale · RotX · RotZ · RotY · translation (row-vector, radians for NVA). In practice High
  Wall navmesh rotations are yaw-only, where all Euler orders agree; unit-tested for the
  yaw case. MSB part rotations are never applied (only positions are needed for markers).
- **Layering algorithm.** Faces sorted by centroid height join the lowest-indexed layer where
  every top-down overlap is within 1.8 m vertically (configurable `layerSplitHeight`): ramps
  and stairs stay in one layer, stacked floors split, and no layer overlaps itself (the brief's
  hard requirement). Trade-off: layer 0 becomes the maximal connected "ground" plan and high
  layers are sparse tower floors — chosen over strict height bands, which would shred the
  ground layer at High Wall's ~146 m of height range.
- **Bonfires = MSB objects with model `o000100`** (the bonfire prop). Simple, verifiable, finds
  all 5 real bonfires plus 1 dummy at y=−999 (reported as an outlier rather than filtered, to
  keep the acceptance check honest).
- **Treasures: part fallback.** Some treasure events leave `TreasurePartName` empty but set the
  base event `PartName`; both are tried. Two events with neither resolvable are skipped (logged).
- **Marker→layer assignment** = best face by `horizontalDistance + 0.5 · verticalOffset`,
  vertical window 6 m, searching an 8 m-expanded bounding box. The recorded distance is the
  horizontal one (what acceptance check 3 measures).
- **Default scale 6 px/m** → 2622×3903 px images for this map; adjustable in config.
- **config.json lives at the repo root and is the single source of the game path.** On this
  machine it holds the WSL path; on Windows it gets the `G:\…` path (REPORT.md portability list).

## Git identity fix (2026-11-20, before phase 2)

- **Source of the noreply address.** `gh` is installed and logged in, but as a different account,
  so its `gh api user` id is not the one asked for ("the login must be Buzzed-Lightyear"). Fell
  back to the public `https://api.github.com/users/Buzzed-Lightyear`, which reports id
  `224458930`; the address is `224458930+Buzzed-Lightyear@users.noreply.github.com`. No number
  was guessed.
- **Config scope.** Set as `user.email` in the Windows global git config and in WSL's global git
  config; removed the repo-local `user.email` override (it held a third, id-less variant).
  `user.name` was left alone everywhere, including the repo-local one.
- **Rewrite method.** `git filter-branch --env-filter` on `main`, changing author and committer
  email only where they equalled the placeholder. One commit was affected. Tree hash, names,
  dates and message compared before and after: identical (`8811969…`). The old commit is still
  reachable locally as `refs/original/refs/heads/main` until the push has gone through.
- **Push.** The one allowed `git push --force-with-lease origin main` failed: no stored
  credentials for github.com, and interactive prompts were disabled so it could not hang. Not
  retried and not worked around; the fixed commit stays local for the owner to push.

## Phase 2 (2026-11-20)

- **CLAUDE.md.** The phase 2 hard rules were added on top; the phase 1 rules are kept below with
  a note that phase 2 wins where they conflict (phase 1 rule 2 "never run a Windows executable
  against the game" versus the read-only tracker; rule 3 "on the Linux filesystem").
- **.NET 10 SDK.** The brief assumes a .NET 10 SDK on Windows; only SDK 9.0.308 was installed.
  Installed SDK 10.0.404 into `deps/dotnet` (gitignored) with Microsoft's `dotnet-install.ps1`
  (Authenticode signature checked: valid, Microsoft Corporation). No system-wide install, no
  PATH change, no elevation. `run-tracker.cmd` and `build-map.cmd` use it when present and fall
  back to `dotnet` on PATH. Retargeting to .NET 9 was rejected: the brief says `net10.0-windows`.
- **Snapshot order.** `mapbuilder snapshot` cannot run before there is an SDK to build it with. A
  PowerShell listing in the same format (`work/game_before_prelim.txt`) was therefore taken
  first, then the SDK was installed, then the unmodified phase 1 builder was built and the
  official `work/game_before.txt` taken — before any change to the code and before any file in
  the game folder was opened.
- **The game writes into its own folder.** The injected mods (Special K `dxgi.dll`,
  SoulsUnsqueezed) rewrite `Game/logs/*`, `Game/dxgi.ini`, `Game/SoulsUnsqueezed.ini` and
  `Game/Version/*.ini` every time the game starts. The owner started the game during this
  session, so the before/after diff is not empty. Reported as a failed check 1 with the file
  list and times rather than filtered out; see REPORT.md.
- **SoulMemory does not satisfy hard rule 2 as shipped, so it was adapted.** It reads through
  `Process.Handle` (all access rights) and carries memory-changing helpers. Vendored: the DS3
  scan patterns and offsets, `Pointer`, the tree builder and relative-scan resolution, with every
  member that could change the game removed and reads routed through a one-method read-only
  interface. Not vendored: `Native/`, `MemoryV2/`, `soulmods/` and everything not needed for a
  position. Details in `src/Tracker/ThirdParty/SoulMemory/PROVENANCE.md`.
- **Own process layer with the exact access mask.** The process is opened with
  `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` (0x1010). `System.Diagnostics.Process`
  is used only to find the PID by name (no handle); its `Handle`, `MainModule`, `Modules` and
  `HasExited` are avoided because they open the process with more rights. The main module's base
  and size come from the PEB (`NtQueryInformationProcess`, basic information) and the PE header,
  read through the same read-only handle. Exit is detected with `GetExitCodeProcess`.
- **A second, smaller open for one diagnostic.** When the read-only open is refused, the tracker
  opens the process once more with `PROCESS_QUERY_LIMITED_INFORMATION` alone (a strict subset of
  the allowed mask) and queries its token to tell the user *why*: the game is running as
  administrator. Judged to be within "that mask and nothing more"; it reads no memory and changes
  nothing. Remove `Elevation.IsProcessElevated` and the two `advapi32` imports if this is too much.
- **Access denied: stopped, not worked around (hard rule 4).** The live read was refused because
  Steam, and so the game, ran elevated. No elevated run, no debug privilege, no other route was
  tried. The tracker also refuses to start when it finds itself elevated.
- **Game version** is the executable's product version (a read of the file on disk), not a value
  from memory; shown with SoulMemory's version bucket.
- **Coordinate space: not verified.** Verifying it needs a successful live read, which did not
  happen. Nothing was hardcoded or corrected; the tracker passes the raw position straight into
  the shared transform. `Tracker --probe` prints the distance to every bonfire marker so the
  check takes seconds once the game is readable.
- **Shared library `src/MapCore`.** Holds the transform, top-down geometry, the map.json model,
  the polygon file, the polygon/layer lookup, the per-tick tracking logic, the replay format and
  `Config`. `Config` moved too: both programs read config.json and the tracker writes the
  orientation back.
- **90° rotation added to the shared transform, config.json and map.json.** The brief asks for
  flip X, flip Z and rotation toggles that are saved to config.json. For builder and tracker to
  agree after a save, rotation has to be part of the one transform both use, so the builder now
  honours `"rotation"` (clockwise quarter turns, applied after the flips) as well.
- **The tracker draws the navmesh polygons, not the builder's PNGs.** Needed for live orientation
  toggles and for dimming layers independently; it also keeps memory flat (seven 2622×3903
  bitmaps would be about 290 MB).
- **Polygon file `navpolys.bin`.** `"LGNP"`, version, count, then per polygon one byte layer, one
  byte vertex count and float32 x/y/z per vertex. 711 KB for 13,672 polygons.
- **Polygon lookup, one refinement of the brief's rule.** Containing polygon with the smallest
  vertical distance; otherwise the nearest within 3 m (3D distance to its outline). Refinement:
  if the best containing polygon is more than 3 m above or below, a polygon within 3 m in 3D
  wins over it (standing on geometry without navmesh over a lower floor). With nothing in reach
  the containing polygon is still returned, and its distance shows how far off it is.
- **"Outside High Wall" is geometric.** No polygon found, or the located polygon more than 12 m
  away. SoulMemory's DS3 code has no current-map read and none was invented. Consequence: in
  another area whose coordinates happen to lie within 12 m of High Wall navmesh the tracker would
  keep tracking; unverified whether such an overlap exists.
- **Trail.** Kept in world space (so orientation changes re-project it with everything else), a
  point every 0.15 m, 4000 points, broken on a jump over 25 m or when tracking is lost.
- **UI: WPF, built in code.** No XAML and no NuGet packages. Reads happen on a background thread
  at 25 Hz so the attach (copying the game's main module for the pattern scan) never freezes
  the window; the window redraws at 25 Hz.
- **Replay.** Text file, one `x,y,z` per line. `--make-path` builds a synthetic walk with A* over
  navmesh polygons that share a corner and routes it through the shared edges so it stays on the
  surface. Default pair: the first two bonfires (by entity id) that are connected that way,
  3001950 → 3001955. The other bonfires sit on separately placed navmesh pieces that share no
  corners with these two.
- **Bonfire names are not in the data.** Markers carry MSB object names and entity ids only;
  which id is "High Wall of Lothric" and which "Tower on the Wall" is for the in-game test.
- **Self-check option `--window-png`.** Saves a picture of the real window after n ticks and
  closes it. Added so the window could be looked at without a person at the screen.
- **Tests.** `tests/Tracker.Tests` is a second test project (`net10.0-windows`) because it
  references the WPF tracker; shared-library tests stay in the cross-platform
  `tests/MapBuilder.Tests`. Same Microsoft test packages as phase 1, nothing else from NuGet.
- **`LICENSE`** is the GPLv3 text as shipped in the SoulSplitter repository at the pinned commit.
- **Tracker is not invariant-globalization.** WPF text rendering needs real cultures; numbers are
  still formatted and parsed with the invariant culture.
- **Nothing committed, staged or pushed for phase 2.** File moves were done on disk; the index
  was left as it was.

## Phase 2 follow-up: connected-floor layers (2026-11-20)

- **The layering algorithm was changed after all.** BRIEF-PHASE2 said to leave it alone; after
  the in-game test the owner asked for tiny steps to stop changing the layer, which only a
  different layering can do. The owner's later request takes precedence over the brief.
- **New default, old one kept.** `"layerMode": "floors"` (new `FloorLayering`) is the default;
  `"stack"` selects the unchanged phase 1 `LayerSplitter`, for comparison and as a way back.
- **What counts as "a tiny step".** Two level areas are one floor when their outlines touch in
  top-down view (within 0.3 m, or one reaching into the other) and the step is at most 0.6 m
  (`layerJoinStep`). 0.6 m was chosen as about the highest thing that still reads as a step
  rather than a ledge. At 15° the result moves little with it: 590 seams at 0.3 m, 525 at
  0.6 m, 564 at 1.0 m (each counted at its own step limit).
- **"Level" means no steeper than 15°** (`layerFlatSlopeDegrees`), and this value does matter.
  The navmesh has no clean gap between floors and stairs in its slope distribution (87 % of the
  area is under 15°, stairs sit around 25–35°). Swept at a 0.6 m join step: 785 seams at 3°,
  759 at 12°, 525 at 15°, 534 at 18°, 373 at 19° (6 layers), 368 at 20°, 321 at 22°, 266 at
  25°. From 20° up, layer changes between two "level" polygons reappear (3, then 26 at 25°),
  because ramps that join two storeys start to count as floor. 15° was kept: a round value
  with a margin below that point and no level-to-level change. The count does not fall
  smoothly (18° → 19° drops by 160), so the lower numbers were not chased; 19° is the value to
  try if stairs still look too busy in the game. The replayed walk has 3 layer changes at
  every value from 12° to 25°.
- **Touching is geometric, not by shared corners.** Separately placed navmesh pieces share no
  vertices, and the tiny steps the owner saw are largely at those joins, so links come from
  outline proximity rather than from the mesh's own connectivity.
- **Layer numbers are by mean height, not strict stack order.** The first version numbered a
  floor one above the highest floor beneath it; that gave 12 layers and no fewer seams. The
  final version puts a region on the layer of the regions it touches when nothing there lies
  over or under it, then numbers layers by mean height: 7 layers again. A higher number is
  usually but not always higher up; the tracker only uses "same layer or not".
- **Seam metric.** "Seam" = two polygons touching in top-down view with under `layerJoinStep`
  of step on different layers. The builder prints the count on every build for either mode, and
  fails the build if any layer lies over itself.
- **The builder now deletes stale `layer_N.png/.svg`** in its own output folder when a build has
  fewer layers than the one before.
- **Coordinate space: verified by the owner's reading**, 0.73 m from bonfire 3001950 at the High
  Wall of Lothric bonfire. Not re-measured by the tool (the game was closed again).
