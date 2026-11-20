# Testing the tracker in the game (High Wall of Lothric)

The tracker only reads. It never starts, stops, focuses or sends input to the game, and it
refuses to run as administrator.

## Before you start

1. **Steam must not be running as administrator.** On 2026-11-20 Steam was elevated, so the game
   was too, and Windows refused the tracker's read-only open ("access denied"). Quit the game and
   Steam, then start Steam normally: no "Run as administrator", and no "Run this program as an
   administrator" tick under Properties → Compatibility on `steam.exe` or `DarkSoulsIII.exe`.
2. The map data must exist in `out\m30_00_00_00\` (`map.json`, `navpolys.bin`). It is there on
   this machine. To regenerate it, run `.\build-map.cmd` from the repository folder. It reads the
   game archives in place and writes only to `out\` and `work\`.
3. The .NET 10 SDK is in `deps\dotnet\` (not installed system-wide). The two `.cmd` scripts find
   it on their own. If `deps\` is ever deleted, install the .NET 10 SDK and they will use that.

## Steps

1. **Start the game yourself**, in borderless or windowed mode, and load a character at the
   **High Wall of Lothric** bonfire. Then start the tracker from a normal, non-elevated prompt
   or by double-clicking:

   ```
   .\run-tracker.cmd
   ```

   The first start compiles the tracker and takes a little longer. Put the tracker window next
   to the game, or tick **Always on top**.

2. **Confirm it is attached and on a bonfire.**
   - The readout on the right says `status   attached (PID …)` and shows a `version`.
   - `world` shows three numbers that hold still while you stand still.
   - The red dot sits on an orange bonfire circle (the circle stays visible around the dot), and
     `polygon` says `inside, 0.0x m`.
   - Write down the `world` x / y / z you see at the bonfire. This is the check that the game's
     coordinates and the map's coordinates are the same space. Done once on 2026-11-20:
     (−18.6, 41.23, −14.44), 0.73 m from the bonfire marker.

   If the dot is on the map but not on a bonfire marker, or the message says you are outside High
   Wall while you are standing at the bonfire, stop here and send me the `world` numbers.

3. **Check the orientation.** Walk forward a few steps, then turn right and keep walking.
   - The blue trail must bend **clockwise**.
   - If it bends anticlockwise the map is mirrored: tick **Flip X** (or **Flip Z**; either one
     un-mirrors it, they differ by a half turn), check again, then press
     **Save orientation to config.json**.
   - If the map is the right way round but you would like north (or the way you are facing)
     elsewhere, use **Rotate 90°** and save. Rotation never mirrors.
   - Map, markers, dot and trail turn together; nothing should be left behind when you toggle.
   - After saving, `.\build-map.cmd` redraws the PNG/SVG images in `out\` the same way round. The
     tracker itself does not need that.

4. **Walk to Tower on the Wall**, using at least one staircase and one ladder on the way. Note:
   - whether the bright (highlighted) floor changes when you change floors, and whether it is
     the floor you are actually on. `layer` in the readout is the number of the bright one;
   - any place where the dot leaves the map, the message "outside High Wall of Lothric" appears,
     or `polygon` shows `nearest` or a distance of more than about a metre for long;
   - whether the dot ends on a bonfire circle again at Tower on the Wall.

## What the window shows

- **Bright floor**: the layer you are on. **Dim floor**: all other layers. A connected floor
  stays on one layer, small steps included, and the layer changes on stairs and ladders, at the
  top, the bottom or part-way up. Several floors that do not lie over each other share a layer,
  so other rooms light up together with yours. If the bright floor changes while you walk on the
  level, note where: that is a bug to report. After `.\build-map.cmd`, restart the tracker to
  load the new layers.
- **Orange circle**: bonfire. **Yellow diamond**: treasure. Markers on other layers are faded.
- **Red dot**: you. **Blue line**: the trail. **Clear trail** empties it.
- **Follow player** keeps you centred. **Fit map to window** shows everything. The mouse wheel
  or the slider zooms. Dragging the map looks around and turns follow off.
- Messages appear over the map when the game is not running, when no character is loaded, when
  you are outside High Wall, or when access is denied. Each clears by itself when the cause goes.

## Quick checks without the window

Run these from the repository folder (they use the same code as the window):

```
.\run-tracker.cmd --probe --seconds 5
```

Attaches, samples the position for five seconds and prints it with the distance to every
bonfire marker. With the character at a bonfire, expect `RESULT: OK` and
`nearest bonfire marker: … within 3 m`. Exit code 2 means the game is not running, 3 means
access denied.

```
.\run-tracker.cmd --make-path work\replay_path.txt
.\run-tracker.cmd --replay work\replay_path.txt
.\run-tracker.cmd --replay work\replay_path.txt --png work\replay.png
```

The first writes a synthetic walk between two bonfires, the second plays it in the window
without the game, the third saves the final view as a picture.

## What to send back

- The `world` x / y / z at the High Wall bonfire and at Tower on the Wall.
- Which way the trail bent in step 3, and the orientation you ended up saving.
- Where the highlighted layer was wrong or the dot left the map in step 4 (a screenshot of the
  tracker window is enough).
- Anything the readout said that was not `attached`.
