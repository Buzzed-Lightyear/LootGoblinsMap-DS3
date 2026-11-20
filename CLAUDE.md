# Rules for all work in this repo

Consolidated from BRIEF-PHASE3.md. This supersedes the phase 1 and phase 2 rules that used to be
in this file, including "High Wall only" and "do not commit".

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

## Environment

- Native Windows. .NET 10 SDK in `deps/dotnet` (not installed system-wide); use `deps\dotnet\dotnet.exe`, `.\run-tracker.cmd`, `.\build-map.cmd`.
- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
- `work/`, `out/`, `deps/`, `bin/`, `obj/` are gitignored.
- Branch for phase 3: `phase3-full-map` (from `phase2-tracker`). Commit locally as you go. Never push unless the owner asks.
- Read `REPORT.md`, `DECISIONS.md` and `TESTING.md` before changing anything.
