# History

The project was built in four phases. These three files are the working record, kept as they
were written at the time. They are not kept up to date; for how things are now, read the
pages one folder up.

| File | What it is |
| --- | --- |
| [REPORT.md](REPORT.md) | The report at the end of each phase: what was built, the result of every check, what was left open |
| [DECISIONS.md](DECISIONS.md) | Every decision that was not obvious, with its reason, in the order it was made |
| [TESTING.md](TESTING.md) | How to test in the game, phase by phase |

| Phase | Outcome |
| --- | --- |
| 1 | A map of one area built from the game's files: archives read in place, navigation mesh decoded, layers, bonfires and treasures placed |
| 2 | A live tracker for that area: the player's position read from the running game, read-only, shown on the map. Later in the phase, layers that follow connected floors. |
| 3 | Every area including the DLC, item names and pickup flags, and the overlay: minimap, full map, hotkeys, settings |
| 4 | One installable program, CI and releases, themes, documentation |

Reading notes:

- File names in these documents refer to the repository as it was then. Until phase 4 these
  three files sat at the top of the repository, and each phase had a written brief that is
  not part of the repository.
- "The tracker" is what is now the app, `LootGoblinsMap.exe`. Until phase 4 it was a separate
  program next to the map builder.
- Numbers such as polygon counts and timings are from the machine and game version the work
  was done on (game version 1.15.2).
