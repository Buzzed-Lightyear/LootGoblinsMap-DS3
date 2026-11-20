# Brief, phase 3: all base-game areas, item names and pickup state, overlay mode

## Goal

Turn the High Wall tracker into a usable loot map for the whole base game.

1. Maps for every base-game area on this install, switching automatically as I move.
2. Treasure markers show the item's name and disappear once picked up.
3. Bonfires and areas have their real names.
4. An overlay mode: a transparent, click-through minimap over the game window, plus a full-map toggle.

Stretch goal: bosses, shown until defeated.

Out of scope: rendering inside the game process, DLL injection, DLC areas (not installed), NPCs, enemy drops, shop and quest items, bonfire-menu settings.

Work without asking me questions. When something is ambiguous, make the most reasonable choice, log it in `DECISIONS.md`, and continue.

## Environment

- Native Windows. .NET 10 SDK (already in `deps/dotnet`).
- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
- Create branch `phase3-full-map` from `phase2-tracker`. Commit locally as you go. Never push.
- Read `REPORT.md`, `DECISIONS.md` and `TESTING.md` first.
- The game may be running, not elevated, with my character sitting at the High Wall of Lothric bonfire.

## Rules

Before anything else, replace `CLAUDE.md` with one consolidated rules file containing this section. It supersedes the phase 1 and phase 2 rules in that file, including "High Wall only" and "do not commit".

1. Never write, move, rename, delete or unpack anything inside the game path. Snapshot it before any other step and again at the end. Compare everything except the eleven files the phase 2 report showed the running game's mods rewriting (`Game/logs/*`, `Game/dxgi.ini`, `Game/SoulsUnsqueezed.ini`, `Game/Version/*.ini`). Any other difference is a failure: stop and report it.
2. The tracker only ever reads the game process, with the same access mask as phase 2. Every name on the banned list in `ReadOnlyGuaranteeTests` stays banned.
3. Phase 3 may add native calls for these purposes only:
   - styling and placing the tracker's own window (click-through, no-activate, topmost);
   - registering and unregistering global hotkeys;
   - read-only queries about the game window's position, size, minimized state and whether it is in the foreground;
   - reading controller state.
   Update the allow-list in the test to exactly what you use, with a one-line reason for each addition in `DECISIONS.md`. Never send messages or input to the game window, and never change its style, position, focus or z-order.
4. Never launch, close, focus or send input to the game or Steam.
5. Do not run the tracker elevated.
6. The tracker makes no network connections.
7. Dependencies come from source in their official repositories, pinned to a commit. Microsoft packages are the only allowed NuGet packages. Record each license.
8. No game data in git. Item names, descriptions, coordinates, params and images are derived at build time into `out/`. Do not put tables of game text in source or tests; tests use synthetic data.
9. Read archives in place. Keep `work/` under 500 MB.

## Part A: all base-game maps

1. **Discover the maps.** Find which map IDs exist in the archives by probing path hashes for their `.msb` files. Do not rely on a fixed list. DLC archives are absent on this install: skip those maps and list them in the report.
2. **Build each map** with the existing pipeline into `out/<map>/`. Run the phase 1 acceptance checks per map (polygon count and bounds, markers near polygons, at least one bonfire where the area has one, transform round trip, no layer lying over itself) and collect the results in one table. A failing map is reported, not hidden, and the others still build.
3. **Answer the world-space question offline.** Using every map's navmesh in its own coordinates, test whether any two maps occupy the same space. Report the overlapping pairs, if any. This decides whether position alone can identify the map.
4. **Current map at runtime.** Read the game's current map or area ID from memory. Find the field from open-source references (SoulMemory, public cheat tables), record the source, and verify it live if the game is running: at the High Wall bonfire it must say `m30_00`. If no reliable field is found after three distinct approaches, fall back to picking the map by position, which is only valid if step 3 found no overlaps. Say plainly which method shipped.
5. **Image bounds.** Stray navmesh pieces far from the main body must not inflate the image. Crop the bounds; keep the polygons in the data.
6. **Names.** Resolve area names and bonfire names from the game's own text and params at build time. Remove the hardcoded "High Wall" labels. Known answer: bonfire entity 3001950 is "High Wall of Lothric" (confirmed in game in phase 2).
7. **Index.** Write `out/index.json` listing every built map with its name and bounds.

## Part B: item names and pickup state

Verify these hints before relying on them.

1. **Item lots.** `Data0.bdt` is believed to be the encrypted regulation holding `ItemLotParam`: the items, counts and acquisition flag for each lot. Read it in place. Param layouts are available in Paramdex (`soulsmods/Paramdex`).
2. **Text.** Item names come from the English message archives (FMG). English only for now, but keep the language a config value.
3. **Resolution.** For each treasure marker, store the resolved items (name, category, count) and the acquisition flag ID. Lots can hold several items and can chain; handle both. Mark anything unresolved as unresolved instead of guessing.
4. **Categories.** A small set for filtering, for example weapons, armor, rings, spells, key items, upgrade materials, consumables, souls. Derive them from the game data.
5. **Runtime.** Read event flags with SoulMemory's DS3 flag reading (source-vendored, read-only subset, same rules as before). Poll the current map's marker flags about twice a second. Collected markers are hidden by default, with a setting to show them dimmed.
6. **Known-answer live checks**, if the game is running at the High Wall bonfire:
   - the lit flag of the High Wall of Lothric bonfire reads true;
   - the Iudex Gundyr defeated flag reads true (he must be beaten to get here);
   - flag reads are stable over five seconds.
7. **Stretch: bosses.** Arena position, name and defeated flag; shown until defeated.

## Part C: overlay mode

- `run-tracker.cmd --overlay` starts it. Keep the phase 2 window as a debug mode.
- **Window:** borderless, transparent, topmost, click-through, never takes focus. It sits exactly over the game window's client area, follows it when moved or resized, and hides when the game is minimized or not in the foreground.
- **Minimap:** in a corner, player centered, current layer bright and others dim, markers colored by category. Show name labels only for the nearest few markers so it stays readable. Corner, size, opacity and zoom are settings.
- **Full map:** a hotkey toggles a large centered view fitted to the current area, with all uncollected items, a legend and a count of what is left. Hotkeys step the shown layer up and down.
- **Hotkeys:** configurable global keyboard hotkeys, with defaults that do not collide with the game's default bindings. An optional controller chord, off by default, because the game also receives controller input.
- **Settings:** an ordinary window opened by hotkey: category filters, show collected, minimap options, hotkeys. Persist to a per-user settings file that is not tracked by git.
- **Limits to state in `TESTING.md`:** the overlay cannot appear over exclusive fullscreen, and it cannot stop the game from receiving input while the full map is open.
- **Cost:** cap redraws at 30 per second and report the tracker's CPU and memory use during a replay.

## Automatic test log

So that my in-game test is just playing, have the tracker append to `work/live_checks.log`:
- each time the player comes to rest within 1.5 m of a bonfire marker: time, map ID, area name, bonfire name, distance;
- each time a tracked flag flips: time, map ID, item or boss name;
- each map switch: time, from, to, and the method that detected it.

## Checks to run yourself

1. The snapshot rule above.
2. `dotnet build` and `dotnet test` pass from a clean checkout.
3. The per-map acceptance table, with every failure explained.
4. The overlap analysis from Part A step 3.
5. The read-only scan passes. The diff of the test shows only additions to the allow-list, and the banned list is unchanged.
6. Unit tests on synthetic data: param and text parsing, lot-to-item resolution including chained lots, a flag flip hiding a marker, map switching by ID, overlay placement from window rectangles, hotkey and settings parsing.
7. Replay: a synthetic path that crosses from one map into an adjacent one, with some flags set. Render the minimap and the full map to PNG, open them, and confirm labels are readable, collected markers are hidden and the map switches.
8. Live checks if the game is running: current map ID, the known-answer flags, the position still within 1 m of bonfire 3001950, and the overlay rectangle equal to the game's client rectangle.
9. No game text in git: take twenty resolved item names from `out/` and confirm none appears in any tracked file.

## Outputs

- Updated `TESTING.md`, a phase 3 section in `REPORT.md`, new entries in `DECISIONS.md`.

`TESTING.md` should cover at least:
1. Start the game myself, borderless or windowed and not elevated, then `run-tracker.cmd --overlay`.
2. The minimap appears over the game, the mouse and camera behave as normal, and alt-tabbing away hides it.
3. Pick up one item on High Wall: its marker disappears within about two seconds. Items I already have are not shown.
4. Warp to one bonfire in each area: the map switches and the dot sits on the bonfire marker.
5. Try the full-map hotkey, the layer keys and the settings window.
6. Send back `work/live_checks.log`.

## When to stop

- If the regulation, params or text cannot be decoded after three distinct approaches, ship parts A and C and report on B.
- If the map ID cannot be read and maps overlap in space, ship a manual map-select hotkey and report it.
- If continuing would require breaking a rule, stop.

Otherwise finish and report the result of each check, what could not be verified without me, and open issues.
