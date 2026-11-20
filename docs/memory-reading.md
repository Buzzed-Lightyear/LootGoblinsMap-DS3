# Reading the game, and the read-only guarantee

The tracker needs three things from the running game: where the player is, which area the
game thinks that is, and which items have been picked up. It gets them by reading the game's
memory. This page says exactly what is read and how the program is prevented, by construction
and by test, from doing anything else.

## The guarantee

1. The game process is opened with two rights and no others: **read memory** and **query
   limited information** (`PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`, mask
   `0x1010`). With that handle Windows itself refuses writes, allocation, thread creation and
   everything else.
2. The program **does not contain** the functions that write to another process, allocate in
   it, start threads in it, load code into it, hook it, debug it or suspend it. Not unused:
   absent from the source and from the compiled files.
3. It never sends the game's window a message, a key or a click, never focuses it, moves it
   or changes its style, and never starts or stops the game or Steam.
4. It never runs as administrator. It has a manifest that asks for no elevation and it exits
   if it finds itself elevated.
5. It makes no network connections. The compiled program does not reference the networking
   parts of .NET at all.
6. It never writes into the game's folder. Game files are read in place.

## What is read

| What | How | Used for |
| --- | --- | --- |
| Player position (x, y, z in metres) | A pointer chain from the game's player object | The dot on the map, the layer |
| Whether a character is loaded, and loading screens | Two flags | Showing "no character loaded" instead of nonsense |
| Current map id (four bytes, for example `m30_00_00_00`) | One field of the player object | Choosing which area's map to show |
| Event flags | The game's own flag table, looked up by flag number | "Picked up" for a treasure, "lit" for a bonfire |
| The game's version | The file version of the executable | The readout, and for bug reports |

The addresses are not fixed. On attach the tracker copies the game's main module, searches the
copy for four short byte patterns, and follows what they point at. This part comes from the
SoulSplitter project's SoulMemory library, vendored as source and cut down to these reads
(`src/Tracker/ThirdParty/SoulMemory`, with a `PROVENANCE.md` listing what was taken and what
was deliberately left out, such as the parts of that library that can write).

Reads happen 25 times a second for the position and about twice a second for the flags of the
area being shown.

## The map id and other game versions

Position and flags are found by pattern, so they survive game updates as long as the
surrounding code does not change. The map id is different: it is read at a **fixed offset**
inside the player object, and that offset was verified on game version 1.15.2 only.

So the value is checked before it is trusted. The builder records every map id that exists in
the game's archives. A value read from memory that is not one of them is not a map id; it
means this game build keeps something else at that offset. Then:

- the value is ignored;
- the area is chosen from the position instead, where exactly one area fits (some areas
  overlap in space, and there the shown area simply stays as it is);
- the overlay and the main window say that the game version is not supported.

## When the game cannot be read

| Situation | What the app does |
| --- | --- |
| Game not running | Waits. Attaches by itself when the game starts. |
| Game or Steam running as administrator | Windows denies the read. The app says so and explains how to restart Steam normally. It does not try to get more rights. |
| No character loaded, loading screen | Says so; tracking starts by itself. |
| Patterns not found (unknown game build) | Says the game version is not supported. |
| Exclusive fullscreen | Warns that no overlay can be drawn over it. |

## The window side

The overlay is a transparent window of the app's own, placed over the game's client area. To
do that it asks Windows read-only questions about the game's window (where it is, how big,
minimized or not, in the foreground or not) and styles and places **its own** window
(click-through, never activated, topmost). The functions that style or move a window are
wrapped so they can only be given one of the app's own windows.

Global hotkeys are registered on the app's own window. The optional controller chord reads
the controller state; it does not hide the buttons from the game.

## The complete list of native functions

These 22 are all the program imports, in one file (`src/Tracker/Memory/NativeMethods.cs`):

| Function | Why |
| --- | --- |
| `OpenProcess` | Open the game with the read and limited-query rights only |
| `ReadProcessMemory` | Read |
| `CloseHandle` | Close what was opened |
| `GetExitCodeProcess` | Notice that the game has closed |
| `QueryFullProcessImageNameW` | Path of the game's executable, for its version |
| `NtQueryInformationProcess` | Find the main module without needing more rights |
| `OpenProcessToken`, `GetTokenInformation` | Is this program, or the game, elevated |
| `FindWindowExW`, `GetWindowThreadProcessId`, `IsWindowVisible` | Find the game's window |
| `IsIconic`, `GetClientRect`, `ClientToScreen`, `GetForegroundWindow` | Where the game's window is, and whether it is minimized or active |
| `GetWindowRect` | Read back where the app's own overlay ended up |
| `GetWindowLongPtrW`, `SetWindowLongPtrW`, `SetWindowPos` | Style and place the app's own overlay window |
| `RegisterHotKey`, `UnregisterHotKey` | Global hotkeys, delivered to the app's own window |
| `XInputGetState` | Controller buttons for the optional chord |

## How it is tested

`tests/Tracker.Tests/ReadOnlyGuaranteeTests.cs` runs on every build, locally and in CI. It
covers the app, the shared library, the map builder and the vendored code.

**On the source text**

- None of 37 banned names appears anywhere, comments included: the functions for writing,
  allocating, protecting, creating threads, loading libraries, hooking, debugging, suspending
  and terminating; for sending input or messages; for focusing or showing windows; for
  starting programs; and the wider access-right constants.
- The native functions declared are exactly the 22 above, all in one file. Function pointers
  and run-time library loading, which could get around the list, are not used.
- The open call is private and is called in exactly two places, each with one of the two
  allowed masks.

**On the compiled files**

- The shipped assemblies are exactly `LootGoblinsMap.dll`, `MapCore.dll` and `MapBuilder.dll`.
- The native imports in the compiled app equal the list of 22; the other two assemblies
  import nothing.
- No banned name appears among imports, referenced members or string constants, and the
  .NET members that would start or kill a process or open one with full rights are not
  referenced.
- No networking assembly or type is referenced.

To add a native function, the list in that test has to be changed in the same commit, with a
reason in the decision log. The banned list has not changed since it was written.

## What this does not promise

The program does not change the game, and that is checked. Whether reading a game's memory is
acceptable to its publisher's online service is not something this project can promise. Other
read-only tools for this game work the same way. If in doubt, play offline while using it.
