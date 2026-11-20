# How a map is built

The builder turns the game's own files into the maps the tracker shows. It runs on the user's
computer, against their installed game, the first time the app starts and again whenever the
game's archives change. Nothing it reads is copied into this repository or into a release.

```text
archives (.bhd/.bdt, read in place)
   |  1. find the maps
   |  2. pull three files per map into the cache
   v
navmesh  ------------->  3. polygons in world space
map file ------------->  4. bonfires and treasures
regulation + text ----->  5. what each treasure holds, its name, its pickup flag
   |
   v
6. layers   7. markers onto layers   8. transform   9. checks
   |
   v
maps/<map id>/map.json + navpolys.bin,  maps/index.json,  maps/build_report.md
```

The entry point is `MapBuildService.BuildAll` in `src/MapBuilder`. All maps of the game take
about 15 to 20 seconds.

## 1. Find the maps

The game keeps its files in archives: a header (`.bhd`) that maps a hash of each internal
path to a place in a data file (`.bdt`). There is no directory listing. `MapDiscovery` asks
the headers for every plausible map file path (`m00_00` to `m99_99`) and builds what answers.
So there is no fixed list of maps in the code: an install without the DLC simply has fewer.

The archives are opened for reading only and read in place. The base game has `Data1` to
`Data5`; the DLCs add `DLC1` and `DLC2`. A missing DLC archive is not an error.

## 2. Extract what a map needs

Three files per map are decompressed into the cache folder:

| File | What it is |
| --- | --- |
| `<map>.msb` | The map file: every object and event placed in the area |
| `<map>.nva` | Where each navigation mesh piece sits in the world |
| `<map>.nvmhktbnd` | The navigation mesh pieces themselves (Havok format) |

If a mod overrides one of these files (Mod Engine style), the override wins, as it does in the
game. The scan for overrides only looks; it changes nothing.

## 3. The navigation mesh

The navigation mesh is what the game's characters use to find their way: convex polygons
covering every place one can walk. `NavGeometry` decodes each piece and moves it to its place
in the world. The result is a list of polygons with 3D corners, in metres.

**This is all the map knows about the shape of an area.** There are no walls, roofs or
scenery in it. The map is therefore a floor plan of walkable ground, nothing more.

## 4. Bonfires and treasures

From the map file:

- **Bonfires** are the objects that use the bonfire model.
- **Treasures** are the "treasure" events. Each names the object it lies on (a corpse, a chest)
  and one or two item lots.

Enemies, their drops, NPCs and merchants are not read.

## 5. Names, contents, flags

Two more sources, shared by all maps:

- **The regulation file** (`Data0.bdt`) holds the game's parameter tables. The builder reads
  five of them: item lots (what a treasure gives, and the event flag set when it is taken),
  goods, weapons, special effects (to recognise souls) and the bonfire table (name and "lit"
  flag of each bonfire).
- **The text files** give the names of items, bonfires and areas, in the game's English.

`ItemResolver` follows a treasure's item lots to items, names them, sorts each into a
category, and records the pickup flag. The weapon table tells shields, catalysts and
ammunition apart from weapons proper. An item lot that does not exist in the tables marks the
treasure as an unused leftover: it is kept in the data and never shown.

If the regulation or the text cannot be read (a game version with another layout), the maps
are still built, without names, and the app says so.

## 6. Layers

A 3D area has to become flat pictures. Polygons are grouped into layers so that no layer lies
over itself. This has a page of its own: [layering.md](layering.md).

## 7. Markers onto layers

Each bonfire and treasure is assigned to the layer of the polygon under it: the nearest
polygon below or just above, within a few metres.

- A treasure with **no polygon in reach** (it lies on a ledge or a roof the navigation mesh
  does not cover) is placed on the layer of the polygon nearest to it in height, looking
  further and further out. It is flagged `offMesh` in the data. There are six in the game.
- A marker that sits in one map's file but stands on a neighbouring map's floor is moved to
  that neighbour.

## 8. The transform

`MapTransform` (in MapCore) turns world metres into picture pixels: 6 pixels per metre, X to
the right, Z down, with optional flips and quarter turns. The picture covers the main body of
the area plus a 3 m margin; a stray polygon far from everything else stays in the data but
does not stretch the picture.

The same class, fed the same numbers from `map.json`, places the player in the tracker. That
is why markers and the player always agree. Height (Y) picks the layer and nothing else.

## 9. Checks and the report

Every build checks itself and writes `build_report.md` next to the maps: per map, the number
of polygons and layers, how many markers were placed, named and given a flag, and whether
each check passed. It also works out which maps overlap each other in space (25 of 190 pairs
do), which is why the tracker reads the current map from the game instead of guessing it from
the position.

## What is written

See [file-formats.md](file-formats.md) for the exact layout.

| File | Content |
| --- | --- |
| `maps/<map id>/map.json` | Name, bounds, transform, layers, markers |
| `maps/<map id>/navpolys.bin` | Every polygon with its layer, for locating the player and drawing |
| `maps/index.json` | All maps, every map id the game has, and the archives used |
| `maps/build_report.md` | The checks |

## Running it by hand

In a development checkout the builder has a command line of its own:

```text
.\build-map.cmd                    all maps into out\
.\build-map.cmd list               only list what the archives contain
```

The app's windowless first run does the same into the data folder:
`LootGoblinsMap.exe --build-maps | more`.
