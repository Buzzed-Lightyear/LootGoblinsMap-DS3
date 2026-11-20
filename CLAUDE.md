# Rules for all work in this repo

Consolidated from BRIEF-PHASE3.md, with the rule changes of BRIEF-PHASE4.md at the end. This
supersedes the phase 1 and phase 2 rules that used to be in this file, including "High Wall only"
and "do not commit".

1. Never write, move, rename, delete or unpack anything inside the game path. Snapshot it before any other step and again at the end. Compare everything except the eleven files the phase 2 report showed the running game's mods rewriting (`Game/logs/*`, `Game/dxgi.ini`, `Game/SoulsUnsqueezed.ini`, `Game/Version/*.ini`). Any other difference is a failure: stop and report it.
2. The tracker only ever reads the game process, with the same access mask as phase 2 (`PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`). Every name on the banned list in `ReadOnlyGuaranteeTests` stays banned.
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

## Phase 4 rule changes (docs/history/BRIEF-PHASE4.md)

Every rule above still holds, except that pushing is now allowed within these limits:

1. You may push `phase3-full-map` and `phase4-release`, fast-forward `main` to the finished work, and push version tags. Never force-push. Never rewrite pushed history.
2. Before every push, confirm no game data is tracked: nothing from `out/` or `work/`, no game file formats, no extracted text or images.
3. Release artifacts are built only by GitHub Actions from the repository, never uploaded from this machine. CI has no game files, so a release cannot contain game data.
4. Workflows use GitHub-owned actions only, pinned to a commit hash, with the smallest permissions that work. No secrets beyond the built-in token.
5. One bundled font is allowed: an open-licensed (OFL) serif taken from its official repository, license recorded. No other third-party assets, and no textures, icons or fonts taken from the game.

## Environment

- Native Windows. .NET 10 SDK in `deps/dotnet` (not installed system-wide); use `deps\dotnet\dotnet.exe`, `.\run-tracker.cmd`, `.\build-map.cmd`.
- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
- `work/`, `out/`, `deps/`, `bin/`, `obj/` are gitignored.
- Branch for phase 4: `phase4-release` (from `phase3-full-map`). Commit as you go.
- Read `docs/history/REPORT.md`, `docs/history/DECISIONS.md` and `docs/history/TESTING.md` before changing anything.
- The packaged app keeps maps, cache, settings and logs under `%LocalAppData%\LootGoblinsMap`; the repo's `config.json` is the development setup.
