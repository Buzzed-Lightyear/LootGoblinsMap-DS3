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
