# Brief, phase 4: installable release, CI/CD, docs, themed maps

## Goal

Make the project something I can download from a GitHub release, run, and playtest, without the repo or an SDK.

1. A packaged Windows app that builds its maps from the user's own game install on first run.
2. GitHub Actions for build, test and tagged releases.
3. A polished README, technical material moved into `docs/`, and a simple project page.
4. Maps and overlay restyled to sit comfortably beside the game's look.
5. Optional, do last and in this order: bosses (the phase 3 stretch goal: position, name, shown until defeated), then controller bindings.

Out of scope: rendering inside the game process, DLL injection, an installer, auto-update, code signing, NPCs.

> Amended by the owner on 2026-11-20, before work started:
>
> 1. **DLC areas are in scope.** They are already built; keep them, and make sure the app also works on an install without the DLC.
> 2. **Map id validation.** The current map id is read at a fixed offset that was only verified on game version 1.15.2. Treat a value that is not a known map id as an unsupported game build: fall back to position where that is unambiguous, and show a clear message.
> 3. **Fix before the first release:** show the 6 treasures that have no navmesh polygon in reach (place them on the nearest layer by height), and split the "weapons" category so shields, ammunition and catalysts are separate.
> 4. **Optional tail**, in this order and only if everything else is done: bosses, then Part E controller bindings.

Work without asking me questions. When something is ambiguous, make the most reasonable choice, log it in `DECISIONS.md`, and continue.

## Starting point

- Phase 3 (`BRIEF-PHASE3.md`) must be finished first: branch `phase3-full-map` exists and `REPORT.md` has a phase 3 section. If it is not finished, finish it before starting this brief.
- Create branch `phase4-release` from `phase3-full-map`.
- Native Windows, .NET 10 SDK in `deps/dotnet`. Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`

## Rule changes

Update `CLAUDE.md` first. Every phase 3 rule still holds except the ban on pushing, which becomes:

1. You may push `phase3-full-map` and `phase4-release`, fast-forward `main` to the finished work, and push version tags. Never force-push. Never rewrite pushed history.
2. Before every push, confirm no game data is tracked: nothing from `out/` or `work/`, no game file formats, no extracted text or images.
3. Release artifacts are built only by GitHub Actions from the repository, never uploaded from this machine. CI has no game files, so a release cannot contain game data.
4. Workflows use GitHub-owned actions only, pinned to a commit hash, with the smallest permissions that work. No secrets beyond the built-in token.
5. One bundled font is allowed: an open-licensed (OFL) serif taken from its official repository, license recorded. No other third-party assets, and no textures, icons or fonts taken from the game.

## Part A: packaged app

1. **One entry point.** A single app the user starts (`LootGoblinsMap.exe`), with the map builder available inside it. The builder's command line stays for development.
2. **Self-contained.** Publish for win-x64 so no .NET install is needed.
3. **Data folder.** Maps, cache, settings and logs live in a per-user data folder (for example under `%LocalAppData%\LootGoblinsMap`), never next to the executable and never in the game folder.
4. **First run.** Find the game without help: read Steam's library configuration (read-only) and locate Dark Souls III; fall back to a folder picker. Validate the folder, then build all maps with a visible progress display. Record how long the build takes on this machine.
5. **Rebuild.** Detect when the game's archives have changed since the last build and offer to rebuild. Also a manual rebuild button.
6. **Clear failure messages** for: game not found, game or Steam running elevated, exclusive fullscreen, an unsupported game build (memory scans fail), missing DLC archives.
7. **Version.** Taken from the git tag at build time and shown in the settings window.
8. **Playtest feedback tools:**
   - a hotkey that marks "something is wrong here": appends time, version, map, layer, world position and the nearest markers to `feedback.log`;
   - the phase 3 `live_checks.log`, now in the data folder;
   - an "export feedback" button that zips logs, settings and version info (no maps) to the desktop;
   - an "open data folder" button.

## Part B: CI/CD

1. **`ci.yml`** on every push and pull request: build and run all tests on a Windows runner.
2. **Game-data guard** in CI: fail if any tracked file has a game data extension, sits under `out/` or `work/`, or is larger than a few MB.
3. **`release.yml`** on tags matching `v*`: build, test, publish, zip the app together with `LICENSE`, a third-party notices file and a short readme, compute SHA-256, and create a GitHub release with those files attached. Tags containing a hyphen are marked pre-release.
4. **`pages.yml`**: deploy the project page from `main`. If Pages cannot be enabled from the command line, say so and tell me the one setting to change.
5. **Prove it.** Push the branch and watch the CI run until it is green. Then fast-forward `main`, tag `v0.1.0-alpha.1`, and watch the release run. Download the released zip into an empty temp folder, verify its hash, unzip it, and run a smoke test that needs no game (version output, and a replay on a synthetic map rendered to PNG).

Cut `alpha.1` as soon as parts A and B are green. Cut `alpha.2` at the end if parts C to E changed anything a user would notice.

## Part C: README, docs, project page

1. **`README.md`**, written for a player, short:
   - what it is, with a screenshot slot at the top;
   - features;
   - install and first run;
   - requirements (Windows 10 or 11 x64, Dark Souls III on Steam, borderless or windowed mode, Steam not run as administrator);
   - controls table;
   - safety: read-only, no injection, no network, builds maps from your own files; playing offline is recommended and use is at your own risk;
   - known limits;
   - a short "build from source" pointer into `docs/`;
   - license, credits for vendored projects, and a line that this is an unofficial fan project not affiliated with FromSoftware or Bandai Namco.
2. **`docs/`** takes everything technical: architecture, the map pipeline, layering, memory reading and the read-only guarantee, file formats, development and testing. Move `REPORT.md`, `DECISIONS.md`, `TESTING.md` and the briefs under `docs/` (a history or dev-log folder) and fix every link. `CLAUDE.md` stays at the root. Add `CHANGELOG.md`.
3. **Project page** in `site/`: one static page, no build step, no external requests. What it is, how it works in three steps, the safety points, a download button to the latest release, a link to the repo. Dark and restrained, matching the app's theme.
4. **Images.** Do not commit screenshots of the game or renders of real maps. Use renders of a synthetic map from the headless mode, and leave clearly named placeholders where I can add real screenshots myself.

## Part D: themed maps and overlay

The aim is a map that looks like it belongs beside the game's interface: dark, muted, ink-on-parchment, serif lettering. An original design in that spirit, not a copy of the game's UI and using none of its assets.

1. **Theme file.** All colors, line weights, fonts, icon sizes and opacities in one theme definition, so I can tune from playtest feedback without code changes. Keep the current look available as a "plain" theme.
2. **Floors.** A soft wash fill with a darker ink outline, heavier on outer edges. Lower and non-current layers fade back. Upper layers cast a faint shadow so height reads at a glance.
3. **Stairs and ramps.** The layering already knows which polygons are steep; draw them with step or hatch lines.
4. **Texture.** Subtle procedural paper grain and a vignette on the full map, generated deterministically. The minimap sits on a translucent dark disc with a thin border.
5. **Icons.** Original vector icons in one consistent style: bonfire, item categories, boss. Readable at minimap size.
6. **Lettering.** The bundled serif for labels and legend. A brief area-name banner when the map switches.
7. **Review renders.** Produce headless PNGs of the minimap and full map on the synthetic map in both themes, open them, and iterate until labels are readable and nothing clutters. Also render two real areas into the data folder (not the repo) for me to look at.

State the limit in the docs: the map is drawn from walkable navmesh, so it is a stylized floor plan without walls, buildings or landmarks.

## Optional: bosses (before Part E)

Only if everything above is done. The phase 3 stretch goal: arena position, name and defeated flag, shown until defeated.

## Part E: controller bindings (optional, do last)

Skip this part if anything earlier is unfinished.

1. Configurable bindings for the overlay's actions (toggle full map, layer up and down, mark feedback, toggle minimap), read by polling controller state only.
2. Support chords and long presses, since the game receives every button too. Pick defaults that do nothing, or nothing harmful, in the game's default layout, and say which you chose and why. Off until enabled in settings.
3. Note in the docs that controllers not exposed as XInput will not be seen.

## Checks to run yourself

1. The game folder snapshot rule from phase 3.
2. `dotnet build` and `dotnet test` pass from a clean checkout, locally and in CI.
3. The read-only scan passes, with the banned list unchanged.
4. The released zip passes the smoke test from Part B step 5, and contains the license and notices.
5. First run on this machine from the released zip, into a fresh data folder: game found automatically, all maps built, time recorded.
6. No game data in git, and none in the release zip (list its contents in the report).
7. Every link in `README.md`, `docs/` and the project page resolves.
8. Theme renders reviewed as in Part D step 7.
9. If the game is running: the packaged app attaches, shows the overlay, and the phase 3 live checks still pass.

## When to stop

- If a push or a workflow run needs credentials or a repository setting you cannot change, stop that step, leave everything ready, and tell me the exact command or setting.
- If continuing would require breaking a rule, stop.

Otherwise finish and report: the release URL, the result of each check, what I need to do by hand, and open issues.
