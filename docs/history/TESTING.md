# Testing the packaged app (phase 4)

From phase 4 on, test with the released program, not from the repository. The steps written
for phase 3 are kept below; what they describe still works the same way, but paths and
commands changed. The current developer guide is `docs/development.md`.

## Play test with the release

1. Download the newest zip from the releases page and unzip it outside the game folder.
2. Start `LootGoblinsMap.exe` (not as administrator). The first run finds the game and builds
   the maps. The window shows how many maps were built and how long it took.
3. Press **Start overlay**. Start the game in borderless or windowed mode, with Steam not run
   as administrator, and load a character.
4. Play. What to look at:
   - **Pick up an item that is on the map.** Its marker should disappear within a second.
     This is the one thing from phase 3 that has not been confirmed in play yet.
   - Walk into another area: the area's name appears for a few seconds and the map changes.
   - Stairs: the bright floor should change on the stairs, not on a level floor.
   - `F7` full map, `Ctrl+PageUp` / `Ctrl+PageDown` layers, `F8` settings, `F9` hide.
   - Settings, "Look": switch between the parchment and the plain theme.
   - Shields, catalysts and ammunition have their own switches under "Items shown".
5. Where something looks wrong, press `F10`. A note with your position and the markers around
   you goes into the feedback log.
6. Afterwards press **Export feedback** and send the zip from your desktop. It holds the logs
   and your settings; no maps and nothing from the game.

## What changed for the phase 3 steps below

| Phase 3 | Now |
| --- | --- |
| `work\live_checks.log` | `%LocalAppData%\LootGoblinsMap\logs\live_checks.log` (with `run-tracker.cmd`: `work\appdata\logs\live_checks.log`) |
| `%APPDATA%\LootGoblinsMap\settings.json` | `%LocalAppData%\LootGoblinsMap\settings.json`; the old file is copied over once |
| `.\run-tracker.cmd` opens the debug window | `.\run-tracker.cmd` opens the app; add `--debug` for the debug window, `--overlay` for the overlay alone |
| Output of `--probe` and `--overlay-check` appears in the console | The app is a windowed program: add `--out <file>` or `\| more` |

## Checks without playing

With the game running and a character loaded, from the unzipped release:

```text
LootGoblinsMap.exe --probe --seconds 5 --out probe.txt
LootGoblinsMap.exe --overlay-check --out overlay.txt
```

Both only read. `--probe` ends with a `RESULT` line; exit code 0 is fine, 2 means the game is
not running, 3 access denied (the game or Steam is elevated).

Without the game:

```text
LootGoblinsMap.exe --data C:\Temp\lgm-demo --make-demo
LootGoblinsMap.exe --data C:\Temp\lgm-demo --overlay --replay C:\Temp\lgm-demo\demo_walk.txt
```

plays a walk across a made-up pair of areas in the real overlay, over your desktop.

---

# Testing the loot map in the game

The tracker only reads. It never starts, stops, focuses or sends input to the game, and it
refuses to run as administrator. Your in-game test is just playing; the tracker writes what it
sees to `work\live_checks.log`, which is what I need back.

## Before you start

1. **Steam must not be running as administrator.** An elevated game cannot be read by an
   unelevated tracker ("access denied"). Start Steam normally: no "Run as administrator", and no
   "Run this program as an administrator" tick on `steam.exe` or `DarkSoulsIII.exe`.
2. **Use borderless or windowed mode.** The overlay is an ordinary window on top of the game. It
   **cannot appear over exclusive fullscreen**: Windows does not let another window draw there.
3. The maps must exist in `out\` (20 map folders and `index.json`). They are there on this
   machine. To rebuild them, run `.\build-map.cmd`; it reads the game archives in place and
   writes only to `out\` and `work\`. It takes about 20 seconds.
4. The .NET 10 SDK is in `deps\dotnet\`. The `.cmd` scripts find it on their own.

## Steps

1. **Start the game yourself**, borderless or windowed and not elevated, and load your character.
   Then, from a normal (non-elevated) prompt in the repository folder:

   ```
   .\run-tracker.cmd --overlay
   ```

   A console window stays open; it shows the hotkeys and any problem (for example a hotkey
   another program already uses). Close it, or press Ctrl+C in it, to stop the tracker.

2. **The minimap appears over the game**, top right, a second or two after the tracker has
   attached. Check that:
   - the mouse and the camera behave exactly as without it (clicks go through the minimap);
   - the game keeps the keyboard focus;
   - alt-tabbing away hides the minimap, and coming back shows it again;
   - the name at the top of the minimap is the area you are in.

3. **Pick up one item on High Wall.** Its marker disappears within about two seconds. Items you
   already have are not shown at all. (Settings → "Show collected items dimmed" brings them back
   faintly, if you want to see what you have done.)

4. **Warp to one bonfire in each area.** After the loading screen the map switches to the new
   area and the red dot sits on a bonfire marker. Stay seated for two seconds so the visit is
   logged. Areas with a map: every area with a bonfire, including the three DLC areas if you can
   reach them. Nothing needs writing down; the log records each visit with its distance.

5. **Try the keys.**

   | Key | What it does |
   |---|---|
   | F7 | Full map: a large centred map of the current area with every item not yet collected, a legend and a count of what is left. F7 again closes it. |
   | Ctrl+PageUp / Ctrl+PageDown | While the full map is open: show the layer above / below. |
   | F8 | Settings window: category filters, collected items, minimap corner, size, opacity and zoom, hotkeys, controller chord. "Apply and save" takes effect at once. |
   | F9 | Hide or show the overlay. |

   The settings window is an ordinary window, so the game loses the focus while it is open;
   click the game to go on playing.

6. **Send back `work\live_checks.log`.**

## Limits you should know about

- **Not over exclusive fullscreen.** Use borderless or windowed.
- **The full map does not pause or block the game.** While it is open the game still receives
  everything you press and every stick movement. Open it somewhere safe.
- **Hotkeys are taken away from the game while the tracker runs.** F7, F8, F9 and
  Ctrl+PageUp / Ctrl+PageDown never reach the game (or any other program) until the tracker is
  closed. The defaults are keys the game does not use; change them in the settings if they clash
  with something else.
- **The controller chord is off by default**, because the game receives the same buttons and
  does whatever it normally does with them. It is there for people who have those buttons free.
- **A map is per area.** Where two areas meet, the game decides which one you are in, and the
  map follows it.
- **A few items are not shown**: ones that stand where there is no walkable floor in the game's
  navigation data (6 of 857), and everything that is not a "treasure" in the map files: enemy
  drops, shop and quest items, NPC gifts.
- **Bosses are not shown** (the stretch goal was not done).

## What the overlay shows

- **Bright floor**: the layer you are on. **Dim floor**: the other layers. A connected floor
  stays on one layer; it changes on stairs and ladders.
- **Diamonds**: items, coloured by category (the full map has the legend). Collected ones are
  hidden. **Orange circles**: bonfires, with their names. **Red dot**: you. **Blue line**: your trail.
- The minimap names the few items nearest to you; the full map names as many as fit without
  covering each other.
- A short message replaces the map when the game is not running, no character is loaded, access
  is denied, or you are in an area without a map. Each clears by itself.

## The debug window

`.\run-tracker.cmd` without `--overlay` opens the phase 2 window: a normal window with the map,
orientation controls and a readout (attach status, game version, map id and how it was chosen,
world and pixel position, layer, distance to the navmesh, items left). Use it when something
looks wrong in the overlay.

## Checks without playing

Run from the repository folder; they use the same code as the overlay.

```
.\run-tracker.cmd --probe --seconds 5
```

Attaches, then prints the position, the map id the game reports, the nearest bonfire and its
lit flag, and whether the flag readings were stable. Exit code 2: game not running; 3: access denied.

```
.\run-tracker.cmd --overlay-check
```

Puts an empty overlay over the game and prints both rectangles; they must be equal.

```
.\run-tracker.cmd --make-path work\walk.txt --from-map m30_00_00_00 --to-map m30_01_00_00
.\run-tracker.cmd --overlay --replay work\walk.txt
.\run-tracker.cmd --replay work\walk.txt --png work\full.png --view full
```

The first writes a synthetic walk that crosses from one area into the next and "picks up" a few
items; the second plays it in the overlay over your desktop, without the game (the hotkeys work);
the third saves the full-map view as a picture (`--view minimap` for the minimap).

## Settings and log files

- Settings: `%APPDATA%\LootGoblinsMap\settings.json` (per user, not in the repository). Delete it
  to go back to the defaults. A value the tracker cannot use falls back to its default and is
  reported in the console.
- Test log: `work\live_checks.log`, one line per event:
  - `bonfire`: you came to rest within 1.5 m of a bonfire marker (map id, area, bonfire, distance);
  - `flag`: a tracked flag flipped, that is, an item was picked up or a bonfire lit (map id, name);
  - `map`: the map switched (from, to, and how it was detected).

## What to send back

- `work\live_checks.log`.
- Anything from steps 2 to 5 that did not behave as described, with the area you were in. A
  screenshot with the minimap or full map in it is enough.
- Places where the bright floor changed while you walked on the level.
