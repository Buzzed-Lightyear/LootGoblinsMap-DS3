using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapCore;
using Tracker.Overlay;
using Tracker.Rendering;
using Tracker.Sources;

namespace Tracker;

/// <summary>The modes that run without a window: replay to PNG, synthetic paths, and the live checks.</summary>
internal static class Headless
{
    /// <summary>
    /// Runs a replay script through the same session, renderer and overlay drawing the windows
    /// use and saves the final view as a PNG. Returns a text summary of what the tracker saw.
    /// <paramref name="view"/>: "follow" or "fit" (the debug panel), "minimap" or "full" (the overlay,
    /// drawn over a stand-in backdrop of the given size).
    /// </summary>
    public static string RenderReplay(TrackerCore core, string pngPath, string view, double zoom, int width, int height, int? shownLayer)
    {
        var session = core.Session;
        var source = (ReplaySource)core.Source;
        var c = CultureInfo.InvariantCulture;

        int tracking = 0, outside = 0;
        float worstDistance = 0;
        var runs = new List<(string Map, int Layer, int Ticks)>();   // map and layer along the replay, run-length encoded
        var events = new List<TrackEvent>();
        // A made-up clock, 25 ticks a second, so the log and the "came to rest" timing are repeatable.
        var time = new DateTime(2026, 1, 1, 12, 0, 0);
        while (!source.Finished)
        {
            events.AddRange(core.StepDirect(time));
            time = time.AddMilliseconds(SamplePump.IntervalMs);
            var frame = session.Current;
            if (frame.State == TrackState.Tracking)
            {
                tracking++;
                worstDistance = Math.Max(worstDistance, frame.Distance);
                if (runs.Count > 0 && runs[^1].Map == session.Map.Id && runs[^1].Layer == frame.Layer)
                    runs[^1] = (runs[^1].Map, frame.Layer, runs[^1].Ticks + 1);
                else
                    runs.Add((session.Map.Id, frame.Layer, 1));
            }
            else if (frame.State == TrackState.OutsideMap)
            {
                outside++;
            }
        }
        // Stand still at the end for two seconds, as a player arriving at a bonfire would.
        for (int i = 0; i < 50; i++)
        {
            events.AddRange(core.StepDirect(time));
            time = time.AddMilliseconds(SamplePump.IntervalMs);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (view is "minimap" or "full")
            {
                // A stand-in for the game picture, so the translucent panel is judged against something.
                var backdrop = new LinearGradientBrush(Color.FromRgb(0x4a, 0x52, 0x4a), Color.FromRgb(0x1d, 0x1f, 0x26), 60);
                dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
                var state = new OverlayState { FullMap = view == "full", ShownLayer = shownLayer };
                OverlayView.Draw(dc, core, state, new ViewState { Follow = true }, new ViewState { FitToWindow = true }, width, height, 1.0);
            }
            else
            {
                var overlay = Readout.Lines(session);
                overlay.Add($"replay   {source.Count} positions, {session.Trail.Count(p => p.HasValue)} trail points");
                var viewState = new ViewState { FitToWindow = view == "fit", Zoom = zoom };
                var style = new RenderStyle { Opaque = true, LabelCount = 10, Settings = core.Settings, Title = null };
                core.Renderer.Render(dc, new Rect(0, 0, width, height), session, viewState, style, Readout.Message(session), overlay);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string? dir = Path.GetDirectoryName(Path.GetFullPath(pngPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using (var fs = File.Create(pngPath))
            encoder.Save(fs);

        var sb = new StringBuilder();
        sb.AppendLine(c, $"replay: {source.Count} positions → {tracking} tracking, {outside} off the shown map");
        sb.AppendLine(c, $"map and layer along the way: {string.Join(" ", runs.Select(r => $"{r.Map[..6]}/L{r.Layer}×{r.Ticks}"))}");
        sb.AppendLine(c, $"map switches: {events.Count(e => e.Kind == TrackEventKind.MapSwitch)}, flag flips: {events.Count(e => e.Kind == TrackEventKind.FlagFlip)}, bonfire rests: {events.Count(e => e.Kind == TrackEventKind.BonfireRest)}");
        sb.AppendLine(c, $"largest distance to the located polygon: {worstDistance:F2} m");
        var (left, total) = session.TreasureCount();
        sb.AppendLine(c, $"final map: {session.Map.Id} {session.Map.Name}; items left {left} of {total}; view {view}; image {width}x{height}");
        foreach (var e in events)
            sb.AppendLine("  log: " + LiveCheckLog.Format(e));
        sb.AppendLine($"wrote {Path.GetFullPath(pngPath)}");
        return sb.ToString();
    }

    /// <summary>
    /// Writes a synthetic replay: a walk between two bonfires of one map, or (with a second map)
    /// from a bonfire of the first map across the place where the two maps meet to a bonfire of
    /// the second. A few treasures passed on the way are "picked up" (their flags turn on), and
    /// two are marked as collected from the start.
    /// </summary>
    public static string MakePath(MapSet maps, string fromMapId, string? toMapId, string outPath, int? fromEntity, int? toEntity, float step)
    {
        var a = maps.Get(fromMapId) ?? throw new ArgumentException($"Map {fromMapId} is not built.");
        var script = new ReplayScript();
        var notes = new StringBuilder();
        var c = CultureInfo.InvariantCulture;

        MapDocument.MarkerInfo Bonfire(MapData map, int? entity, Vector3 near)
        {
            var bonfires = map.Markers.Where(m => m.IsBonfire && m.Layer >= 0).ToList();
            if (bonfires.Count == 0)
                throw new InvalidDataException($"{map.Id} has no placed bonfire marker.");
            if (entity is { } id)
                return bonfires.FirstOrDefault(b => b.EntityId == id) ?? throw new ArgumentException($"No placed bonfire with entity id {id} in {map.Id}.");
            return bonfires.OrderBy(b => Vector3.Distance(b.WorldPosition, near)).First();
        }

        void AddLeg(MapData map, List<Vector3> positions)
        {
            // The three treasures nearest to the walk are picked up at the moment it is closest to them.
            var pickups = new Dictionary<int, int>();   // step index → flag
            var nearestFirst = map.Markers
                .Where(m => !m.IsBonfire && !m.Unused && m.Layer >= 0 && m.Flag > 0)
                .Select(m =>
                {
                    int best = 0;
                    float bestDistance = float.MaxValue;
                    for (int i = 0; i < positions.Count; i++)
                    {
                        float d = Vector3.Distance(positions[i], m.WorldPosition);
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            best = i;
                        }
                    }
                    return (Marker: m, Step: best, Distance: bestDistance);
                })
                .OrderBy(x => x.Distance);
            foreach (var (marker, at, distance) in nearestFirst)
            {
                if (pickups.Count >= 3)
                    break;
                if (pickups.ContainsKey(at) || pickups.ContainsValue(marker.Flag))
                    continue;
                pickups[at] = marker.Flag;
                notes.AppendLine(c, $"  picks up in {map.Id}: {marker.Label} (flag {marker.Flag}) at step {script.Steps.Count + at}, {distance:F0} m from the walk");
            }
            for (int i = 0; i < positions.Count; i++)
            {
                var replayStep = new ReplayStep { Position = positions[i], MapId = map.Id };
                if (pickups.TryGetValue(i, out int flag))
                    replayStep.FlagChanges[flag] = true;
                script.Steps.Add(replayStep);
            }
        }

        string header;
        if (toMapId == null || toMapId.Equals(fromMapId, StringComparison.OrdinalIgnoreCase))
        {
            var bonfires = a.Markers.Where(m => m.IsBonfire && m.Layer >= 0).OrderBy(m => m.EntityId).ToList();
            if (bonfires.Count < 2)
                throw new InvalidDataException($"{a.Id} has fewer than two placed bonfire markers.");
            var from = fromEntity is { } f ? Bonfire(a, f, default) : bonfires[0];
            var to = toEntity is { } t2 ? Bonfire(a, t2, default) : bonfires.First(b => b != from);
            var positions = NavPath.Between(a, from.WorldPosition, to.WorldPosition, step, out bool along);
            if (!along && fromEntity == null && toEntity == null)
            {
                // No explicit pair: take the first two bonfires that are joined over the navmesh.
                foreach (var x in bonfires)
                {
                    foreach (var y in bonfires.Where(y => y != x))
                    {
                        var candidate = NavPath.Between(a, x.WorldPosition, y.WorldPosition, step, out bool connected);
                        if (!connected)
                            continue;
                        (from, to, positions, along) = (x, y, candidate, true);
                        break;
                    }
                    if (along)
                        break;
                }
            }
            AddLeg(a, positions);
            header = $"Synthetic walk in {a.Id} ({a.Name}) from bonfire {from.Label} to bonfire {to.Label}; " +
                     (along ? "follows connected navmesh polygons." : "straight line (not connected over the navmesh).");
        }
        else
        {
            var b = maps.Get(toMapId) ?? throw new ArgumentException($"Map {toMapId} is not built.");

            // Where the two maps meet: a polygon of the first whose centre lies on the second's navmesh.
            Vector3? meeting = null;
            float bestFit = 1.0f;
            foreach (var poly in a.Polygons)
            {
                var centre = poly.Vertices.Aggregate(Vector3.Zero, (sum, v) => sum + v) / poly.Vertices.Length;
                if (centre.X < b.MinX - 2 || centre.X > b.MaxX + 2 || centre.Z < b.MinZ - 2 || centre.Z > b.MaxZ + 2)
                    continue;
                var hit = b.Locator.Locate(centre);
                if (hit.Found && hit.Distance < bestFit)
                {
                    bestFit = hit.Distance;
                    meeting = centre;
                }
            }
            if (meeting == null)
                throw new InvalidDataException($"{a.Id} and {b.Id} do not share any floor; pick two adjacent maps.");

            // Prefer bonfires that are joined to the meeting place over the navmesh, nearest first.
            (MapDocument.MarkerInfo Marker, List<Vector3> Leg, bool Along) Leg(MapData map, int? entity, bool towardsMeeting)
            {
                var candidates = entity != null
                    ? new List<MapDocument.MarkerInfo> { Bonfire(map, entity, meeting.Value) }
                    : map.Markers.Where(m => m.IsBonfire && m.Layer >= 0).OrderBy(m => Vector3.Distance(m.WorldPosition, meeting.Value)).ToList();
                if (candidates.Count == 0)
                    throw new InvalidDataException($"{map.Id} has no placed bonfire marker.");
                (MapDocument.MarkerInfo, List<Vector3>, bool)? fallback = null;
                foreach (var bonfire in candidates)
                {
                    var path = towardsMeeting
                        ? NavPath.Between(map, bonfire.WorldPosition, meeting.Value, step, out bool along)
                        : NavPath.Between(map, meeting.Value, bonfire.WorldPosition, step, out along);
                    if (along)
                        return (bonfire, path, true);
                    fallback ??= (bonfire, path, false);
                }
                return fallback!.Value;
            }

            var (from, legA, alongA) = Leg(a, fromEntity, towardsMeeting: true);
            var (to, legB, alongB) = Leg(b, toEntity, towardsMeeting: false);
            AddLeg(a, legA);
            AddLeg(b, legB.Skip(1).ToList());
            header = $"Synthetic walk from bonfire {from.Label} in {a.Id} ({a.Name}) to bonfire {to.Label} in {b.Id} ({b.Name}), " +
                     string.Create(c, $"crossing where the maps meet at ({meeting.Value.X:F1}, {meeting.Value.Y:F1}, {meeting.Value.Z:F1}). ") +
                     $"First leg {(alongA ? "along the navmesh" : "straight line")}, second leg {(alongB ? "along the navmesh" : "straight line")}.";
        }

        // Two treasures of the starting map are already collected when the replay starts.
        var walked = script.Steps.SelectMany(s => s.FlagChanges.Keys).ToHashSet();
        foreach (var marker in a.Markers.Where(m => !m.IsBonfire && !m.Unused && m.Layer >= 0 && m.Flag > 0 && !walked.Contains(m.Flag)).Take(2))
        {
            script.InitialFlags[marker.Flag] = true;
            notes.AppendLine(c, $"  already collected at the start: {marker.Label} (flag {marker.Flag})");
        }

        float length = 0;
        for (int i = 1; i < script.Steps.Count; i++)
            length += Vector3.Distance(script.Steps[i - 1].Position, script.Steps[i].Position);
        script.Write(outPath, header + "\nFormat: x,y,z per line; @map <id> and @flag <id> on|off apply from the next position.");
        return header + "\n" + string.Create(c, $"{script.Steps.Count} positions, {length:F1} m\n") + notes + $"wrote {Path.GetFullPath(outPath)}\n";
    }

    /// <summary>
    /// The live read checks: attach (read-only), sample position, map id and flags for a few
    /// seconds and compare with the map data. Returns the process exit code:
    /// 0 fine, 2 game not running, 3 access denied, 4 no character or unsupported, 5 a check failed.
    /// </summary>
    public static int Probe(MapSet maps, Config config, double seconds, IReadOnlyList<int> extraFlags, TextWriter output)
    {
        var c = CultureInfo.InvariantCulture;
        using var source = new LiveGameSource();
        var session = new TrackingSession(maps, config.MapId, config.Orientation);
        PositionSample last = default;

        // Give the attach (module copy + scan) a moment before the sampling window starts.
        var attachDeadline = DateTime.UtcNow.AddSeconds(15);
        do
        {
            last = source.Read();
            if (last.Status is SourceStatus.Ok or SourceStatus.NoCharacter or SourceStatus.AccessDenied)
                break;
            if (last.Status == SourceStatus.GameNotRunning && last.Detail == "Dark Souls III is not running")
                break;
            Thread.Sleep(250);
        } while (DateTime.UtcNow < attachDeadline);

        output.WriteLine("attach: " + (last.Detail ?? last.Status.ToString()));
        if (!string.IsNullOrEmpty(source.AttachLog))
            output.WriteLine(source.AttachLog);

        if (last.Status == SourceStatus.GameNotRunning)
        {
            output.WriteLine("RESULT: SKIPPED — the game is not running.");
            return 2;
        }
        if (last.Status == SourceStatus.AccessDenied)
        {
            output.WriteLine("RESULT: STOPPED — access denied. The tracker does not run elevated; nothing was read.");
            return 3;
        }

        var samples = new List<Vector3>();
        var mapIds = new Dictionary<string, int>();
        var statusCounts = new SortedDictionary<SourceStatus, int>();
        Dictionary<int, bool>? firstFlags = null;
        int flagRounds = 0, flagChanges = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var nextFlags = DateTime.UtcNow;
        while (DateTime.UtcNow < end)
        {
            last = source.Read();
            statusCounts[last.Status] = statusCounts.GetValueOrDefault(last.Status) + 1;
            if (last.Status == SourceStatus.Ok)
            {
                samples.Add(last.Position);
                string id = last.MapId ?? "(none)";
                mapIds[id] = mapIds.GetValueOrDefault(id) + 1;
            }
            session.Update(last);
            if (last.Status == SourceStatus.Ok && DateTime.UtcNow >= nextFlags)
            {
                nextFlags = DateTime.UtcNow.AddMilliseconds(250);
                var watched = session.WatchedFlags().Concat(extraFlags).Distinct().ToList();
                if (source.ReadFlags(watched) is { } states)
                {
                    session.ApplyFlags(states);
                    flagRounds++;
                    if (firstFlags == null)
                        firstFlags = new Dictionary<int, bool>(states);
                    else
                        flagChanges += states.Count(kv => firstFlags.TryGetValue(kv.Key, out bool was) && was != kv.Value);
                }
            }
            Thread.Sleep(SamplePump.IntervalMs);
        }

        output.WriteLine($"version: {last.GameVersion}");
        output.WriteLine($"readings over {seconds.ToString("F1", c)} s: {string.Join(", ", statusCounts.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (samples.Count == 0)
        {
            output.WriteLine("RESULT: NO POSITION — attached, but no character is loaded (or the build is unsupported).");
            return 4;
        }

        bool ok = true;
        bool finite = samples.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
        var min = new Vector3(samples.Min(v => v.X), samples.Min(v => v.Y), samples.Min(v => v.Z));
        var max = new Vector3(samples.Max(v => v.X), samples.Max(v => v.Y), samples.Max(v => v.Z));
        float spread = (max - min).Length();
        var p = samples[^1];
        output.WriteLine(string.Create(c, $"position: x {p.X:F3}  y {p.Y:F3}  z {p.Z:F3}   ({samples.Count} samples); finite {finite}; spread {spread:F4} m"));
        ok &= finite;

        output.WriteLine($"map id from memory: {string.Join(", ", mapIds.Select(kv => $"{kv.Key} ×{kv.Value}"))}  →  shown map {session.Map.Id} {session.Map.Name} (chosen by {session.MapMethod})");
        foreach (string line in Readout.Lines(session))
            output.WriteLine("  " + line);

        var nearest = session.Map.Markers.Where(m => m.IsBonfire && m.Layer >= 0)
            .Select(m => (Marker: m, Dist: Vector3.Distance(m.WorldPosition, p))).OrderBy(x => x.Dist).FirstOrDefault();
        if (nearest.Marker != null)
        {
            output.WriteLine(string.Create(c, $"nearest bonfire marker: {nearest.Marker.Label} (entity {nearest.Marker.EntityId}), {nearest.Dist:F2} m away"));
            if (nearest.Dist <= TrackingSession.BonfireRestDistance && nearest.Marker.Flag > 0 && firstFlags != null &&
                firstFlags.TryGetValue(nearest.Marker.Flag, out bool lit))
            {
                output.WriteLine($"its lit flag {nearest.Marker.Flag} reads {lit}  →  {(lit ? "as expected for a bonfire you are sitting at" : "UNEXPECTED: you are at this bonfire, so it should be lit")}");
                ok &= lit;
            }
        }

        if (firstFlags == null)
        {
            output.WriteLine("event flags: could not be read");
            ok = false;
        }
        else
        {
            int collected = session.Map.Markers.Count(m => !m.IsBonfire && !m.Unused && m.Layer >= 0 && session.IsCollected(m));
            var (left, total) = session.TreasureCount();
            output.WriteLine($"event flags: {firstFlags.Count} read per round, {flagRounds} rounds, {flagChanges} changed readings; " +
                             $"treasures on this map: {collected} collected, {left} of {total} left");
            foreach (int flag in extraFlags)
                output.WriteLine($"  flag {flag} reads {(firstFlags.TryGetValue(flag, out bool on) && on)}");
            ok &= flagChanges == 0;
        }

        bool stable = spread <= 0.05f;
        output.WriteLine(ok && stable
            ? "RESULT: OK — position, map id and flags read; position finite and stable, flags stable."
            : $"RESULT: CHECK — finite={finite}, position stable={stable} (expected a spread under 0.05 m while standing still), flag readings changed={flagChanges}.");
        return ok && stable ? 0 : 5;
    }

    /// <summary>
    /// Live check of the overlay placement: asks Windows where the game's client area is, puts the
    /// (empty, transparent) overlay window over it, and reads back where the overlay really is.
    /// Also confirms that showing the overlay did not change which window is in the foreground.
    /// Exit code 0 equal, 2 game not running, 6 no game window, 7 rectangles differ or focus changed.
    /// </summary>
    public static int OverlayCheck(TextWriter output)
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(LiveGameSource.ProcessName);
        if (processes.Length == 0)
        {
            output.WriteLine("RESULT: SKIPPED — the game is not running.");
            return 2;
        }
        int pid = processes[0].Id;
        nint gameWindow = GameWindow.Find(pid);
        var state = GameWindow.Query(gameWindow);
        if (!state.Found)
        {
            output.WriteLine($"RESULT: no visible window found for the game (PID {pid}).");
            return 6;
        }
        output.WriteLine($"game window 0x{gameWindow:X} (PID {pid}): client area {state.Client}, minimized {state.Minimized}, foreground {state.Foreground}");
        if (state.Minimized || state.Client.IsEmpty)
        {
            output.WriteLine("RESULT: the game window is minimized; the overlay stays hidden and has nothing to cover.");
            return 6;
        }

        nint foregroundBefore = Memory.NativeMethods.GetForegroundWindow();
        var overlay = new OverlayWindow(new System.Windows.Controls.Canvas());
        overlay.Show();
        Pump();
        overlay.Cover(state.Client);
        Pump();
        var actual = overlay.ActualRect;
        long style = overlay.ExtendedStyle;
        nint foregroundAfter = Memory.NativeMethods.GetForegroundWindow();
        var (visible, _) = OverlayLayout.Place(state);
        overlay.Close();

        bool clickThrough = (style & Memory.NativeMethods.WsExTransparent) != 0;
        bool noActivate = (style & Memory.NativeMethods.WsExNoActivate) != 0;
        bool sameRect = actual == state.Client;
        bool focusKept = foregroundBefore == foregroundAfter;
        output.WriteLine($"overlay window: {actual}; click-through {clickThrough}, no-activate {noActivate}, topmost {overlay.Topmost}");
        output.WriteLine($"foreground window before 0x{foregroundBefore:X}, after 0x{foregroundAfter:X} → {(focusKept ? "unchanged" : "CHANGED")}");
        output.WriteLine($"would be shown right now: {visible} (only while the game is in the foreground)");
        output.WriteLine(sameRect && focusKept && clickThrough && noActivate
            ? "RESULT: OK — the overlay rectangle equals the game's client rectangle."
            : $"RESULT: CHECK — rectangles equal {sameRect}, focus kept {focusKept}, click-through {clickThrough}, no-activate {noActivate}.");
        return sameRect && focusKept && clickThrough && noActivate ? 0 : 7;

        static void Pump() =>
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
