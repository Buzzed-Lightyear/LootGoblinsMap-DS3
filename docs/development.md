# Development and testing

## What you need

- Windows 10 or 11, 64-bit.
- The .NET 10 SDK.
- For anything beyond the tests: Dark Souls III installed through Steam. The tests, the demo
  maps and CI need no game files at all.

## Build and test

```text
dotnet build LootGoblinsMap.slnx -c Release
dotnet test  LootGoblinsMap.slnx -c Release
```

The solution has three projects in `src/` and two test projects in `tests/`; see
[architecture.md](architecture.md).

## The rules

These hold for every change. Most are enforced by a test or by CI; the rest are on you.

**The game is never changed.**

- Nothing is ever written, moved, renamed, deleted or unpacked inside the game's folder.
  Archives are read in place.
- The game process is only ever read, with the read and limited-query rights. The banned
  list in `ReadOnlyGuaranteeTests` stays as it is.
- Native calls are allowed for four purposes only: styling and placing the app's own window;
  registering global hotkeys; read-only questions about the game's window; reading the
  controller. The allow-list in the test must equal exactly what the code imports.
- The app never starts, stops, focuses or sends input to the game or Steam.
- The app never runs elevated.
- The app makes no network connections.

How the rules about the game process, elevation and the network are tested: [memory-reading.md](memory-reading.md).

**No game data in the repository.**

- Item names, descriptions, positions, map shapes, parameters and images are derived at
  build time on the user's machine. No tables of game text or numbers in source or tests;
  tests use made-up data. No game screenshots and no pictures of real maps.
- `tools/check-no-game-data.ps1` fails on game file types, on the builder's output files, on
  anything under `out/` or `work/`, on images outside `site/images/` and `docs/images/`, and
  on files over 2 MB. CI runs it on every push; run it yourself before pushing.

**Dependencies.**

- Third-party code is vendored as source from its official repository, pinned to a commit,
  with its license and a `PROVENANCE.md`. Microsoft's packages are the only NuGet packages
  allowed (today: the test framework). One bundled font, under the Open Font License. No
  other third-party assets, and nothing taken from the game.

**Releases.**

- Release files are built only by GitHub Actions from a tag, never uploaded from a
  developer's machine. Workflows use GitHub's own actions, pinned to a commit hash, with the
  smallest permissions that work and no secrets beyond the built-in token.
- Pushed history is never rewritten.

## Working without the game

Everything can be tried on a made-up pair of maps that the app generates itself:

```text
set DATA=%TEMP%\lgm-demo
set APP=dotnet src\Tracker\bin\Release\net10.0-windows\LootGoblinsMap.dll

%APP% --data %DATA% --make-demo
%APP% --data %DATA% --debug   --replay %DATA%\demo_walk.txt
%APP% --data %DATA% --overlay --replay %DATA%\demo_walk.txt
%APP% --data %DATA% --replay %DATA%\demo_walk.txt --png %DATA%\walk.png --view full
```

- `--make-demo` builds two small areas ("Goblin Keep" and "Mossy Cellars") through the real
  export and writes three replay scripts: a walk across both areas, a climb, and a descent.
- `--debug` is an ordinary window with the map and a readout of everything the tracker reads.
- `--overlay --replay` shows the real overlay over your screen, with working hotkeys.
- `--png` renders without a window. This is what the tests and the release smoke test do, and
  how every picture in the documentation was made.

The app is a windowed program, so a console does not show what it prints. Add `| more`, or
`--out <file>`.

## Working with the game

`config.json` in the repository root is the development setup: it names the game folder and
sends maps to `out/` and the extraction cache to `work/` (both ignored by git). Set `gamePath`
to your install.

```text
.\build-map.cmd                     build every map into out\
.\run-tracker.cmd                   the app, using config.json
.\run-tracker.cmd --overlay         the overlay on its own
.\run-tracker.cmd --debug           the debug window
.\run-tracker.cmd --probe --out work\probe.txt
.\run-tracker.cmd --overlay-check --out work\overlay.txt
```

With `--config`, the app keeps its own files (settings, themes, logs) in `work\appdata`.

**Live checks.** With the game running, not elevated, and a character loaded:

- `--probe` attaches, samples the position, the map id and the flags for a few seconds, and
  compares them with the map data: is the position finite and steady, is the map id a known
  one, is the bonfire you are sitting at lit, do the flags hold still. It ends with a `RESULT`
  line and an exit code (0 fine, 2 game not running, 3 access denied, 4 no character,
  5 a check failed).
- `--overlay-check` puts an empty overlay over the game and reads back where it ended up: the
  overlay's rectangle must equal the game's client rectangle, and the foreground window must
  be the same before and after.

Neither sends anything to the game.

**Playing.** Start the overlay and play. `logs\live_checks.log` records every area change,
pickup and bonfire rest with the time, which is the record of a play test. Press the feedback
hotkey (F10) where something looks wrong.

**The game folder check.** To prove a session changed nothing in the game's folder, take a
listing before and after and compare:

```text
dotnet run --project src\MapBuilder -c Release -- snapshot "<game folder>" work\before.txt
... work ...
dotnet run --project src\MapBuilder -c Release -- snapshot "<game folder>" work\after.txt
fc work\before.txt work\after.txt
```

The running game, and mods if you have any, rewrite a few log and settings files of their own
in that folder; anything else differing would be a failure.

## Tests

| Project | What it covers |
| --- | --- |
| `tests/MapBuilder.Tests` | File format readers on synthetic files, geometry, layering, item resolution on made-up parameter tables, the tracking session (area switches, pickups, unknown map ids), settings, hotkeys, overlay placement, Steam library parsing, rebuild detection, the feedback note |
| `tests/Tracker.Tests` | Builder and tracker put the same point on the same pixel; the memory reader against a fake process image; themes; the demo maps rendered in every theme; the feedback zip; and the read-only guarantee on source and compiled files |

No test needs the game, a window on screen, or the network.

## Adding things

**A native function.** Declare it in `src/Tracker/Memory/NativeMethods.cs` (the only file
allowed to), add it to `AllowedImports` in `ReadOnlyGuaranteeTests` with a comment saying which
of the four purposes it serves, and note the reason in the decision log. If it changes a
window, make it private and reachable only through a wrapper that takes an `OwnWindow`.

**A theme field.** Add the property with its "plain" default to `Theme`, set it in
`Parchment()`, use it in `MapRenderer`, and add a row to [themes.md](themes.md). Old theme
files keep working because missing fields take the default.

**An item category.** `ItemCategories` in `MapCore/MapIds.cs` (the list, the order and the
display name), the resolver in `MapBuilder/GameData/ItemResolver.cs`, a colour in both themes
and an icon in `MapRenderer`.

## CI and releases

Three workflows in `.github/workflows`:

| Workflow | When | What |
| --- | --- | --- |
| `ci.yml` | Every push and pull request | Game-data guard, build, test, then package and smoke-test (the package is thrown away) |
| `release.yml` | A pushed tag `v*` | The same, then a GitHub release with the zip and its SHA-256. A tag with a hyphen (`v0.1.0-alpha.1`) becomes a pre-release. |
| `pages.yml` | A push to `main` that changes `site/` | Publishes the project page |

To release:

1. Add a section to [CHANGELOG.md](../CHANGELOG.md).
2. Make sure CI is green on the commit and that it is on `main`.
3. `git tag -a v1.2.3 -m "Loot Goblins Map 1.2.3"` and `git push origin v1.2.3`.

The version shown in the app comes from the tag. `tools/package.ps1` is what the workflows
run; you can run it yourself to try the packaging, but its output is never uploaded by hand.

GitHub's hosted runners are administrator accounts, and the app refuses to run as
administrator. So on a runner the smoke test checks the version, the shipped files and that
refusal; the rendering is covered there by the test suite. On a normal account
`tools/smoke-test.ps1 -AppDir <unzipped release>` runs the whole thing.

**The project page** needs one repository setting, once: Settings, Pages, "Build and
deployment", Source = "GitHub Actions".

## Layout

```text
src/MapCore/        shared model (no UI)
src/MapBuilder/     archives -> maps; ThirdParty/SoulsFormats is vendored
src/Tracker/        the app; ThirdParty/SoulMemory is vendored; Fonts/ holds the bundled font
tests/              the two test projects
tools/              game-data guard, packaging, smoke test
packaging/          the short readme that ships in the zip
site/               the project page (static, no build step)
docs/               these pages; docs/history holds the reports of each phase
```
