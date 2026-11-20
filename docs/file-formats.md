# File formats

Every file the program writes or reads of its own. The game's formats are not described here;
the builder reads those through the vendored SoulsFormats code.

All JSON files use camelCase names, allow comments and trailing commas when read, and ignore
fields they do not know. Numbers always use a point as the decimal sign.

## The data folder

`%LocalAppData%\LootGoblinsMap`, unless `--data <folder>` or the `LOOTGOBLINSMAP_DATA`
environment variable says otherwise.

```text
LootGoblinsMap\
  app.json               what the app remembers: game folder, orientation, last build
  settings.json          what to show and which keys do what
  maps\
    index.json           all maps
    build_report.md      the checks of the last build
    m30_00_00_00\
      map.json           one area
      navpolys.bin       its walkable polygons
    ...
  cache\                 files extracted from the game's archives during a build
  themes\
    builtin\             reference copies of the built-in themes (rewritten by the program)
    my-theme.json        themes of your own; one named parchment.json replaces the built-in one
  logs\
    live_checks.log      what the tracker saw: area changes, pickups, bonfire rests
    feedback.log         the notes dropped with the feedback hotkey
    last_build.log       the full output of the last map build
```

`maps\` and `cache\` are derived from the game. They are never part of a feedback export.

## map.json

One area. Written by the builder and read by the tracker through the same class
(`MapCore.MapDocument`).

| Field | Meaning |
| --- | --- |
| `map` | The game's map id, for example `m30_00_00_00` |
| `name` | The area's name from the game's text; absent when the game has none |
| `source`, `dlc` | The archive the map came from, and whether that is a DLC archive |
| `bounds` | World-space box of the main body: minX, maxX, minY, maxY, minZ, maxZ (metres) |
| `strayPolygonCount` | Polygons far from the main body, kept in the data but outside the picture |
| `axisConvention` | Which world axis runs along which picture axis, with flips and rotation, in words and as values |
| `transform` | `originWorldX`, `originWorldZ`, `pixelsPerMeter`, picture size before and after rotation |
| `navPolygons`, `navPolygonCount` | Name of the polygon file next to this one, and how many polygons it holds |
| `layers[]` | Per layer: `index`, `heightRange` (min, max), `faceCount`, and picture file names when pictures were written |
| `markers[]` | Bonfires and treasures, see below |

A marker:

| Field | Meaning |
| --- | --- |
| `type` | `bonfire` or `treasure` |
| `name`, `entityId`, `itemLots` | The object's name and ids in the game's map file |
| `world` | Position: x, y, z in metres |
| `layer` | The layer it is shown on; -1 when it has none (never shown) |
| `pixel` | Its place in the builder's own pictures |
| `distToLayerPolyM` | Sideways distance to the polygon that gave it its layer |
| `displayName` | A bonfire's name |
| `items[]` | A treasure's contents: `name`, `category`, `count`, `itemId`, `chancePercent` |
| `category` | `key`, `ring`, `weapon`, `shield`, `catalyst`, `armor`, `spell`, `upgrade`, `soul`, `ammo`, `consumable`, `unknown`, or `bonfire` |
| `flag` | The event flag that means "picked up" (treasure) or "lit" (bonfire); 0 when unknown |
| `unresolved`, `notes[]` | Something could not be worked out, and why |
| `unused` | A leftover treasure event whose items do not exist; never shown |
| `offMesh` | No walkable polygon was in reach; the layer is the nearest one by height |
| `fromMap` | The marker is defined in another map's file but stands on this map's floor |

## navpolys.bin

The walkable polygons of one area, in world space. Binary, little-endian.

```text
"LGNP"                      4 bytes, ASCII
version                     int32, currently 1
polygonCount                int32
polygonCount times:
    layer                   1 byte
    vertexCount             1 byte (3 or more)
    vertexCount times:      x, y, z as float32 (metres)
```

## index.json

| Field | Meaning |
| --- | --- |
| `language` | The text language the names were read in |
| `builtUtc` | When the maps were built |
| `mapsOverlapInSpace` | True when at least two areas overlap, so position alone cannot always tell them apart |
| `maps[]` | Per area: `id`, `name`, `directory`, `source`, `dlc`, `bounds`, picture size, counts of layers, polygons, bonfires and treasures, `built`, `checksPassed`, `problem` |
| `knownMapIds[]` | Every map id found in the game's archives, built or not. A map id read from memory that is not in this list is treated as an unsupported game build. |
| `archives[]` | The archives the maps were built from |

## app.json

| Field | Meaning |
| --- | --- |
| `gamePath` | The game's folder |
| `flipX`, `flipZ`, `rotation` | How the map is turned (set with "Save orientation" in the debug window) |
| `builtStamp` | Fingerprint of the archives the maps were built from: a hash over their names, sizes and modified times. A different value on a later start means the game files changed. |
| `builtUtc`, `builtWithVersion`, `buildSeconds`, `mapsBuilt`, `dlcBuilt` | Facts about the last build |

## settings.json

Edited through the settings window; hand edits are fine. A value that is not usable falls
back to its default, one by one, and the window lists what was wrong.

| Field | Default | Meaning |
| --- | --- | --- |
| `hiddenCategories` | `[]` | Categories not shown |
| `showCollected` | `false` | Show picked-up items dimmed instead of hiding them |
| `showBonfires` | `true` | |
| `minimapCorner` | `TopRight` | `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` |
| `minimapSize` | `340` | Screen pixels |
| `minimapMargin` | `24` | Distance from the edges of the game picture |
| `minimapOpacity` | `0.85` | 0.2 to 1 |
| `minimapPixelsPerMeter` | `4` | Zoom of the minimap |
| `minimapLabels` | `4` | How many of the nearest markers are named |
| `fullMapHotkey` | `F7` | |
| `layerUpHotkey`, `layerDownHotkey` | `Ctrl+PageUp`, `Ctrl+PageDown` | |
| `settingsHotkey` | `F8` | |
| `toggleOverlayHotkey` | `F9` | |
| `markFeedbackHotkey` | `F10` | |
| `theme` | `parchment` | Name of a file in `themes\`, without `.json` |
| `controllerChordEnabled` | `false` | |
| `fullMapControllerChord` | `Back+RightThumb` | Two or more buttons joined with `+` |

Hotkeys are written like `F7` or `Ctrl+Shift+M`: optional `Ctrl`, `Alt`, `Shift`, `Win`, then
a letter, a digit, `F1` to `F24`, `PageUp`, `PageDown`, `Home`, `End`, `Insert`, `Delete`, an
arrow, or `Num0` to `Num9`.

## Theme files

See [themes.md](themes.md).

## live_checks.log

One line per event, appended while tracking. Three kinds:

```text
2026-01-01T12:00:07 map from=m90_00_00_00 to=m90_01_00_00 area="Mossy Cellars" method="map id read from memory"
2026-01-01T12:00:09 flag map=m90_01_00_00 area="Mossy Cellars" item="Moss Poultice ×2" flag=90010204 state=on
2026-01-01T12:00:11 bonfire map=m90_01_00_00 area="Mossy Cellars" bonfire="Mossy Hall" distance=0.00
```

`map` is a change of area and how it was decided, `flag` an item's flag changing (a pickup),
`bonfire` the player coming to rest next to a bonfire.

## feedback.log

One block per press of the feedback hotkey: a line with the time, the app's version, the
area, the layer, the world position and how the area was chosen, followed by the nearest
markers with their distance, layer, flag and whether they count as collected.

```text
2026-03-04T05:06:07 mark version=0.1.0 map=m90_00_00_00 area="Goblin Keep" state=Tracking layer=0 world=(10.00, 0.00, 4.00) polygon=Inside:0.00 gameMap=m90_00_00_00 chosenBy="map id read from memory"
       2.0 m  treasure "Gatehouse Key" layer=0 flag=90010101 collected=True
       4.0 m  treasure "Pot Lid Shield" layer=0 flag=90010104 collected=False
```

## The feedback zip

"Export feedback" writes `LootGoblinsMap-feedback-<date>-<time>.zip` to the desktop:

```text
version.txt          app version, Windows version, facts about the last map build
settings.json
app.json
logs\...             everything in the logs folder
build_report.md      the checks of the last build
```

No maps, no cache, nothing from the game.

## Replay scripts

A text file that stands in for the game: positions, the map id the "game" reports, and flag
changes. One entry per line; blank lines and lines starting with `#` are skipped.

```text
# x,y,z in metres
@map m90_00_00_00
@flag 90010101 on
6.0,0.0,6.0
6.5,0.0,6.0
@flag 90010104 on
7.0,0.0,6.0
@map m90_01_00_00
86.0,6.0,14.0
```

- `x,y,z` is one position, 25 to the second.
- `@map <id>` applies from the next position on; `@map none` stops reporting a map id, so the
  tracker has to go by position.
- `@flag <number> on|off` changes a flag at the next position. Flags set before the first
  position are the state the "save" starts in.

`LootGoblinsMap.exe --replay <script>` plays one in the debug window, `--overlay --replay`
over the screen, and `--replay <script> --png <file>` renders the result to a picture.
`--make-demo` writes three such scripts next to the made-up demo maps.
