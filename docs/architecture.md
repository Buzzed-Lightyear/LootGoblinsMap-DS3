# Architecture

Loot Goblins Map does two jobs. It **builds maps** from the game's files, once, and it **tracks**
the player on those maps while the game runs. Both jobs live in one program,
`LootGoblinsMap.exe`.

```text
 game files (read in place)                    running game (memory, read-only)
          |                                                 |
          v                                                 v
   +--------------+     maps on disk      +-----------------------------------+
   |  MapBuilder  | --------------------> |  Tracker (LootGoblinsMap.exe)     |
   |  build once  |   map.json            |  position, map id, pickup flags   |
   +--------------+   navpolys.bin        |  -> session -> renderer -> overlay|
          \           index.json          +-----------------------------------+
           \                                   /
            +----------- MapCore -------------+
              shared model: one transform, one map document, one locator
```

## The three projects

| Project | Builds | Role |
| --- | --- | --- |
| `src/MapCore` | `MapCore.dll` (plain .NET, no UI) | Everything the builder and the tracker must agree on: the map document, the world-to-pixel transform, the polygon lookup, the tracking session, settings. |
| `src/MapBuilder` | `MapBuilder.dll` and a command-line `MapBuilder.exe` | Reads the game's archives and writes the maps. Linked into the app; the command line is for development. |
| `src/Tracker` | `LootGoblinsMap.exe` (WPF) | The app: launcher window, first-run build, live reader, overlay, debug window, settings. |

Tests are in `tests/MapBuilder.Tests` (logic that needs no window) and `tests/Tracker.Tests`
(rendering, the app, the read-only guarantee).

The rule that shapes the split: **anything two programs must agree on exists once, in MapCore.**
The builder computes a marker's pixel with `MapTransform`; the tracker computes the player's
pixel with the same class and the same numbers read back from `map.json`. A test builds a map
and checks that both sides land on the same pixel in all eight orientations.

## What runs when

**First run** (`LauncherWindow`, `AppEnvironment`)

1. Look up where Steam is installed (registry, read-only) and read its `libraryfolders.vdf`
   to find the game. If that fails, ask for the folder.
2. Run `MapBuildService.BuildAll` on a background thread, reporting progress.
3. Store a fingerprint of the archives (names, sizes, times) next to the maps. On later
   starts a different fingerprint means the game was patched or the DLC changed, and the app
   offers a rebuild.

**While tracking** (`TrackerCore`)

```text
LiveGameSource --25 Hz--> SamplePump (own thread) --> TrackingSession --> MapRenderer --> OverlayView
   position, map id        flags about 2 Hz             map, layer,        draws a panel    30 fps at most,
   event flags                                          collected set                       only when changed
```

- `LiveGameSource` attaches to the game process and reads the position, the current map id
  and the event flags. It copes with the game not running, starting later, or closing.
- `SamplePump` does the reading on its own thread so a slow attach never freezes a window.
- `TrackingSession` (in MapCore, no UI) decides which map is shown, which layer the player is
  on, which markers are collected, and records events for the log.
- `MapRenderer` draws one panel in the current theme. The overlay, the debug window and the
  picture export all call it, so what is tested is what is shown.
- `OverlayController` keeps a transparent, click-through window exactly over the game's client
  area, registers the hotkeys, and redraws only the panel and only when something changed.

A `ReplaySource` can stand in for `LiveGameSource`: it plays a text script of positions, map
ids and flag changes. Every test and every picture in the documentation is made that way.

## Where data lives

| What | Where |
| --- | --- |
| The game | Wherever Steam put it. Only ever opened for reading. |
| Maps, extraction cache, settings, themes, logs | `%LocalAppData%\LootGoblinsMap` (override with `--data` or the `LOOTGOBLINSMAP_DATA` variable) |
| The program | Wherever it was unzipped. It writes nothing next to itself. |

In a development checkout, `--config config.json` puts maps in `out/` and the cache in
`work/`, both ignored by git. See [development.md](development.md).

## Entry points

`LootGoblinsMap.exe` without arguments is the app. With arguments it has windowless modes used
by the tests, the release smoke test and the live checks: `--version`, `--build-maps`,
`--make-demo`, `--replay … --png …`, `--probe`, `--overlay-check`, `--export-feedback`.
`LootGoblinsMap.exe --help | more` lists them.

## Dependencies

No NuGet packages in the program. Third-party code is vendored as source, pinned to a commit,
under `src/*/ThirdParty/`, each with a `PROVENANCE.md` that says what was taken and what was
left out. The list is in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
