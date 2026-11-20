# Hard rules, phase 2 (copied from BRIEF-PHASE2.md — binding for all work in this repo)

1. Never write, move, rename, delete or unpack anything inside the game path. Take a snapshot with `mapbuilder snapshot` before any other step and again at the end, and diff them. Any difference is a failure: stop and report it.
2. The tracker only ever reads the game process. Open it with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION` and nothing more. No `WriteProcessMemory`, `VirtualAllocEx`, `VirtualProtectEx`, `CreateRemoteThread`, DLL injection or hooks, anywhere in the code you ship. If a library you vendor has write or inject code paths, leave those files out.
3. Never launch, close, focus or send input to the game or Steam. I start the game myself.
4. Do not run the tracker elevated. If reading fails with access denied, stop and report it instead.
5. The tracker makes no network connections.
6. Dependencies come from source in their official repositories, pinned to a commit. Microsoft packages are the only allowed NuGet packages.
7. Do not commit, push or publish game data or anything derived from it.

Phase 2 environment (from BRIEF-PHASE2.md):

- Native Windows, not WSL. .NET 10 SDK.
- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III`
- `out/`, `work/`, `deps/`, `bin/`, `obj/` are gitignored.
- Scope: High Wall of Lothric (`m30_00_00_00`) only. Do not process any other map, even as a test.
- Do not commit or push phase 2 work; that is left to the repo owner.

# Hard rules, phase 1 (copied from BRIEF.md)

Phase 1 ran on WSL. Where a phase 1 rule conflicts with phase 2 (rule 2's "never run a Windows
executable against the game" versus the read-only tracker; rule 3's "on the Linux filesystem"),
the phase 2 rules above apply. Everything else still holds.

1. Never write, move, rename, delete or unpack anything inside the game path or the mod folder. Open files there read-only.
2. Never run a Windows executable against the game: no UXM, no Mod Engine, no launching the game or Steam.
3. Extract only the files you need into `work/` inside this project, on the Linux filesystem (not under `/mnt/c`), and do all processing there.
4. Before any other step, save a listing of the game path (relative path, size, modified time) to `work/game_before.txt`. At the end, save `work/game_after.txt` and diff the two. Any difference is a failure: stop and report it.
5. Dependencies come from source in their official repositories, pinned to a commit. No prebuilt third-party binaries.
6. Do not commit, push or publish game data or anything derived from it. Add `work/` and `out/` to `.gitignore`.

Additional constraints from BRIEF.md:

- Game path (read-only): `G:\SteamLibrary\steamapps\common\DARK SOULS III` (WSL: `/mnt/g/SteamLibrary/steamapps/common/DARK SOULS III`). Mod folder: none; mods are believed to be inside the game folder. Never modify or remove mod files, DLLs or the executable.
- Do not copy the game or any whole archive into WSL; read archives in place. Keep `work/` under 500 MB; shallow-clone dependencies.
- Scope: High Wall of Lothric (`m30_00_00_00`) only. Do not process any other map, even as a test.
- Keep the code cross-platform: no hardcoded paths, game path from config/argument.
