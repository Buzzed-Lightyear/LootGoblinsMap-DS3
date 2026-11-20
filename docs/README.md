# Documentation

For players, the [README](../README.md) at the top of the repository is the place to start.
These pages are for people who want to know how the program works or want to change it.

| Page | What it covers |
| --- | --- |
| [architecture.md](architecture.md) | The three projects, what runs when, and where data lives |
| [map-pipeline.md](map-pipeline.md) | How a map is built from the game's files: archives, navigation mesh, items, names, checks |
| [layering.md](layering.md) | How a 3D area is split into the layers the map shows |
| [memory-reading.md](memory-reading.md) | What is read from the running game, and how the read-only guarantee is built and tested |
| [file-formats.md](file-formats.md) | Every file the program writes or reads of its own: maps, settings, themes, logs, replay scripts |
| [themes.md](themes.md) | How the map is drawn and what a theme file can change |
| [development.md](development.md) | Building, testing, releasing, and the rules that keep game data out of the repository |
| [history/](history/README.md) | The reports and the decision log of each phase, as written at the time |

Two things are worth knowing before reading any of them:

- **The map is a stylized floor plan.** It is drawn from the game's navigation mesh: the
  polygons that say where a character can walk. There are no walls, buildings or landmarks in
  that data, so there are none on the map.
- **Nothing from the game is in this repository.** Item names, positions, map shapes and
  everything else are derived on the user's computer from their own installed game. The tests
  and all pictures use made-up data.
