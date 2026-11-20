# Brief: DS3 map builder MVP (High Wall of Lothric)

## Goal

Build an offline command-line tool that reads Dark Souls III game data and produces a 2D, per-floor map of High Wall of Lothric with bonfire and item markers.

Out of scope: any overlay, any memory reading, any other area, launching the game.

Scope: High Wall of Lothric only. Do not process any other map, even as a test.

Work without asking me questions. When something is ambiguous, make the most reasonable choice, log it in `DECISIONS.md`, and continue.

## Inputs

- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
  (from WSL: `/mnt/g/SteamLibrary/steamapps/common/DARK SOULS III`)
- Mod folder (read-only): none — mods are believed to be inside the game folder.
- Map: High Wall of Lothric, believed to be `m30_00_00_00`. Verify this.

## Hard rules

Copy this section into `CLAUDE.md` in the project root before doing anything else.

1. Never write, move, rename, delete or unpack anything inside the game path or the mod folder. Open files there read-only.
2. Never run a Windows executable against the game: no UXM, no Mod Engine, no launching the game or Steam.
3. Extract only the files you need into `work/` inside this project, on the Linux filesystem (not under `/mnt/c`), and do all processing there.
4. Before any other step, save a listing of the game path (relative path, size, modified time) to `work/game_before.txt`. At the end, save `work/game_after.txt` and diff the two. Any difference is a failure: stop and report it.
5. Dependencies come from source in their official repositories, pinned to a commit. No prebuilt third-party binaries.
6. Do not commit, push or publish game data or anything derived from it. Add `work/` and `out/` to `.gitignore`.

## Storage

Do not copy the game or any whole archive into WSL. Read the archives in place from the game path and extract only the High Wall files needed into `work/`. Keep `work/` under 500 MB and use shallow clones for dependencies. If the budget cannot be met, stop and report.

## Context to verify, not trust

- The base game ships as encrypted BHD5/BDT archives (`Data1` to `Data5`, plus DLC). Existing open-source readers handle them (Smithbox, the UXM source). BHD5 indexes entries by path hash, so you can hash the specific paths you need instead of needing a full name list.
- This install runs a few QoL mods plus a widescreen/aspect-ratio fix. None are expected to change map files. Still work out how they are installed (a Mod Engine override folder, or loose files in the game folder), and check whether any file needed for High Wall is overridden. If none is, build from the vanilla archives and say so in the report. Resolve each file the way the game would: mod override first, base archive second. Where both copies exist, compare them and log which files differ. Never modify or remove mod files, DLLs or the executable.
- Navmesh: the `.nva` file is believed to hold navmesh placement, with the polygon geometry stored as Havok navmesh data in a per-map binder beside it. Smithbox contains decoders for both.
- DS3 `.dcx` compression should be zlib-based, so no Oodle library should be needed.
- Placement data (bonfires, treasure) is in the map's `.msb`.

## Stack

C# on the current .NET LTS, running on Linux (WSL2). Use SoulsFormatsNEXT plus whichever Smithbox decoders are needed. Render SVG, and rasterize with a cross-platform library. Do not use System.Drawing.

## Portability

The next phase moves to Windows. Keep the code cross-platform: no hardcoded paths; the game path comes from a config file or argument; nothing Linux-only. List anything that would need changing on Windows in `REPORT.md`.

## Pipeline

1. Locate and extract the High Wall navmesh, `.nva` and `.msb` into `work/`.
2. Decode the navmesh polygons and apply their placement transforms so everything is in one map coordinate space.
3. Split polygons into floor layers so that no layer overlaps itself in top-down view. Record each layer's height range.
4. Project each layer top-down. Fill the polygons, and stroke edges that belong to only one polygon.
5. Compute one world-to-pixel transform for the map (origin, scale, axis mapping), shared by all layers.
6. Read bonfire and treasure placements from the MSB, transform them, and assign each to a layer by height. Include the item lot ID. Resolving the pickup flag ID and item names is a stretch goal.

## Outputs

- `out/m30_00_00_00/layer_<n>.svg` and `.png`
- `out/m30_00_00_00/map.json` containing:
  - layers: height range, image file, origin, scale
  - markers: type, IDs, world position, layer, pixel position
  - the axis convention used
- Unit tests
- `REPORT.md` and `DECISIONS.md`

## Acceptance checks (run these yourself)

1. The game path listing is identical before and after.
2. The navmesh decodes to a non-trivial polygon count with plausible bounds (hundreds of metres, not zero and not millions).
3. At least 90% of bonfire and treasure markers lie within 2 m horizontally of a polygon on their assigned layer. List every outlier with its distance.
4. At least three bonfires are found. High Wall of Lothric, Tower on the Wall and Vordt of the Boreal Valley are in this area; naming them is optional.
5. World to pixel to world round-trips within 1 cm in unit tests.
6. Open every PNG and look at it. Each layer should read as a connected floor plan, not scattered fragments. Write one line per layer in the report.
7. `dotnet build` and `dotnet test` pass from a clean checkout.

## Known limit

You cannot confirm orientation (mirroring or rotation) without the running game. Do not claim it is correct. State the axis convention in the report and make flipping it a single config change.

## When to stop

- If the navmesh still does not decode after three distinct approaches, stop and write up what you tried, with the errors.
- If continuing would require breaking a hard rule, stop.

Otherwise finish, then write `REPORT.md` covering: what was built, the result of each acceptance check, which files came from mods, the licenses of any code you vendored, and open issues.
