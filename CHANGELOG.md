# Changelog

What changed for people who use Loot Goblins Map. Newest first.
Versions follow [semantic versioning](https://semver.org); a version with a hyphen (alpha, beta) is a pre-release.

## 0.1.0-alpha.2

### Changed

- A name label no longer covers your own dot on the map. A label that has no room on one side
  of its marker now tries the other side.
- On the parchment full map, floors above the one you are on are easier to tell from the paper.
- The built-in themes are now kept as reference copies in the `themes/builtin` folder and
  are refreshed by every new version. To change one, copy it up into `themes` and edit the
  copy; your file wins. (Alpha.1 put editable copies straight into `themes`, which would have
  kept showing the old look after an update. If you ran alpha.1, delete `parchment.json` and
  `plain.json` from your `themes` folder unless you edited them.)

### New

- Documentation: a readme for players, technical pages under `docs/`, and a project page.

## 0.1.0-alpha.1

The first release you can download and run. Earlier work lived only in the source repository.

### New

- **One program, nothing to install.** Unzip, start `LootGoblinsMap.exe`. It carries everything it needs.
- **First run sets itself up.** It finds the game through Steam, builds the maps from your own
  game files with a progress bar (about half a minute), and remembers how long that took. If it
  cannot find the game it asks for the folder.
- **Rebuilds when the game changes.** After a patch, or when the DLC is added or removed, it
  offers to rebuild the maps. There is also a "Rebuild maps" button.
- **All areas**, including both DLCs when they are installed. Without the DLC everything else works.
- **Minimap and full map** over the game, click-through, with the items you have not picked up
  yet, bonfires, your position and your trail. Items disappear from the map when you pick them up.
- **Item categories you can switch on and off**: key items, rings, weapons, shields, catalysts,
  armor, spells, upgrade materials, souls, ammunition and consumables.
- **Themes.** "Parchment" (ink on aged paper, with icons per category, step lines on stairs and
  a serif typeface) is the default; "plain" is the flat look of the development versions. Themes
  are files you can edit.
- **Area name banner** when you walk into another area.
- **Feedback tools**: a hotkey (F10) that notes "something is wrong here" with your position and
  what is around you, an "Export feedback" button that zips logs and settings to the desktop
  (no maps, nothing from the game), and an "Open data folder" button.
- **Clear messages** when something is in the way: the game or Steam running as administrator,
  exclusive fullscreen, a game version the program does not know, missing DLC archives.

### Changed since the development versions

- Items that lie where the game has no walkable ground nearby (six of them in the whole game)
  are now shown, on the floor nearest to them in height.
- Shields, catalysts and ammunition are no longer lumped in with weapons.
- Settings, maps and logs now live in `%LocalAppData%\LootGoblinsMap`. Settings from a
  development version are carried over.

### Known limits

- Exclusive fullscreen is not supported. Use borderless or windowed mode.
- Tested with game version 1.15.2 only. On another version the program says so and picks the
  area from your position where that is unambiguous.
- The map is a floor plan of where you can walk. It has no walls, buildings or landmarks.
- Bosses, enemies, NPCs and merchants are not shown.
- The program is not signed, so Windows asks before running it the first time.
