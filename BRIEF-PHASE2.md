# Brief, phase 2: live tracker MVP (High Wall, Windows)

## Goal

A Windows desktop program that runs beside Dark Souls III, reads the player's position from the game process, and shows the High Wall map with a live player dot. Its purpose is to let me verify orientation and tracking in the running game.

Out of scope: an in-game overlay (transparent, click-through), any other map, item names, pickup state, changing the layering algorithm.

Work without asking me questions. When something is ambiguous, make the most reasonable choice, log it in `DECISIONS.md`, and continue.

## Environment

- Native Windows, not WSL. .NET 10 SDK.
- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
- This repo already contains the phase 1 map builder (`src/MapBuilder`). Read `REPORT.md` and `DECISIONS.md` first.
- `out/` and `work/` are not in git, so they do not exist on this machine yet.

## Hard rules

Copy this section into `CLAUDE.md` in the project root before doing anything else.

1. Never write, move, rename, delete or unpack anything inside the game path. Take a snapshot with `mapbuilder snapshot` before any other step and again at the end, and diff them. Any difference is a failure: stop and report it.
2. The tracker only ever reads the game process. Open it with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` and nothing more. No `WriteProcessMemory`, `VirtualAllocEx`, `VirtualProtectEx`, `CreateRemoteThread`, DLL injection or hooks, anywhere in the code you ship. If a library you vendor has write or inject code paths, leave those files out.
3. Never launch, close, focus or send input to the game or Steam. I start the game myself.
4. Do not run the tracker elevated. If reading fails with access denied, stop and report it instead.
5. The tracker makes no network connections.
6. Dependencies come from source in their official repositories, pinned to a commit. Microsoft packages are the only allowed NuGet packages.
7. Do not commit, push or publish game data or anything derived from it.

## Part A: repo and builder changes

1. Add a `.gitignore` covering `work/`, `out/`, `deps/`, `bin/`, `obj/`. The repo currently has none.
2. Add a `LICENSE` file with the GPLv3 text, since the repo is public and vendors GPLv3 code. Do not commit or push; leave that to me.
3. Move the code both programs need (map transform, geometry helpers, map.json model) into a shared library project so builder and tracker cannot drift apart.
4. Have the builder also export the navmesh polygons for runtime use: every polygon's world-space vertices and its layer index, in a compact file under `out/<map>/`.
5. Set the Windows game path in `config.json`, then run the builder on Windows to regenerate `out/`. Read the archives in place as before; copy nothing.
6. Leave the layering algorithm alone. Known issue to record in the report: layers are stack order (lowest surface at each spot, then second lowest, and so on), not connected floors, and their height ranges overlap. A layer therefore cannot be chosen from the player's height alone.

## Part B: the tracker (`src/Tracker`)

- **UI:** WPF or WinForms on `net10.0-windows`. An ordinary resizable window with an always-on-top toggle.
- **Memory reading:** use SoulMemory from `FrankvdStam/SoulSplitter` (GPLv3), source-vendored at a pinned commit, DS3 parts only. You need the player position and a way to tell whether a character is loaded. Confirm it satisfies hard rule 2 and adapt it if not.
- **Coordinate space:** verify, do not assume, that the position read from memory is in the same space as the MSB and navmesh coordinates. If there is an offset, find where the game stores it; do not hardcode a correction.
- **Loop:** about 20 to 30 times a second, read the position, find the navmesh polygon under the player (inside it in top-down view with the smallest vertical distance; otherwise the nearest within 3 m), and take that polygon's layer as the current layer.
- **Rendering:** draw all layers together. Current layer at full strength, the others dimmed. Draw bonfire and treasure markers, the player dot, and a trail of recent positions. Follow the player by default, with zoom and a fit-to-window toggle.
- **Orientation controls:** live toggles for flip X, flip Z and 90 degree rotation, plus a button that saves them to `config.json`. They must apply to map, markers, dot and trail together.
- **Debug readout:** attach status, detected game version, raw world x/y/z, pixel x/y, current layer, distance to nearest polygon.
- **Graceful states:** game not running, no character loaded, player outside High Wall. Show a clear message for each and recover automatically.

## Checks to run yourself

1. Game folder snapshot identical before and after.
2. `dotnet build` and `dotnet test` pass from a clean checkout.
3. Unit tests for the polygon and layer lookup, and for builder and tracker producing identical pixel positions for the same world point.
4. A source scan proving the tracker and everything it references declares none of the APIs banned in hard rule 2.
5. Replay mode: the tracker accepts a recorded or synthetic list of positions and can save the rendered view to a PNG. Generate a path between two bonfire markers, render it, open the PNG and confirm the map, markers, dot and trail look right.
6. If `DarkSoulsIII.exe` happens to be running, attach and sample the position for a few seconds. Confirm the values are finite and stable while standing still. If the character is at a bonfire, confirm the position is within about 3 m of one of the bonfire markers. If the game is not running, skip this and say so plainly.

## Outputs

- `src/Tracker`, the shared library, tests
- `TESTING.md`: the exact steps for my in-game test, including how to start the tracker
- A phase 2 section appended to `REPORT.md`, and new entries in `DECISIONS.md`

`TESTING.md` should cover at least:
1. Start the game myself in borderless or windowed mode, load a character at the High Wall of Lothric bonfire, then start the tracker.
2. Confirm it shows attached and the dot sits on a bonfire marker.
3. Walk forward and turn right. The trail must bend clockwise. If it bends the other way the map is mirrored: toggle a flip and save.
4. Walk to Tower on the Wall, and use at least one staircase and one ladder. Note whether the highlighted layer follows, and any place the dot leaves the map.

## When to stop

- If the player position still cannot be read reliably after three distinct approaches, stop and write up what you tried.
- If continuing would require breaking a hard rule, stop.

Otherwise finish and report the result of each check, what could not be verified without me, and open issues.
