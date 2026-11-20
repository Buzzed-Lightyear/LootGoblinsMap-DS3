using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MapCore;
using Tracker.Rendering;
using Tracker.Sources;
using Orientation = MapCore.Orientation;

namespace Tracker;

/// <summary>The modes that run without a window: replay to PNG, synthetic path, and the live read check.</summary>
public static class Headless
{
    /// <summary>
    /// Feeds a list of positions through the same session and renderer the window uses and
    /// saves the final view as a PNG. Returns a text summary of what the tracker saw.
    /// </summary>
    public static string RenderReplay(MapData map, Orientation orientation, IReadOnlyList<Vector3> positions,
                                      string pngPath, ViewState view, int width, int height)
    {
        var session = new TrackingSession(map, orientation);
        using var source = new ReplaySource(positions);

        int tracking = 0, outside = 0, inside = 0, nearest = 0;
        float worstDistance = 0;
        var layersSeen = new SortedDictionary<int, int>();
        var runs = new List<(int Layer, int Ticks)>();   // the layer sequence along the replay, run-length encoded
        while (!source.Finished)
        {
            var frame = session.Update(source.Read());
            if (frame.State == TrackState.Tracking)
            {
                tracking++;
                if (runs.Count > 0 && runs[^1].Layer == frame.Layer)
                    runs[^1] = (frame.Layer, runs[^1].Ticks + 1);
                else
                    runs.Add((frame.Layer, 1));
                layersSeen[frame.Layer] = layersSeen.GetValueOrDefault(frame.Layer) + 1;
                worstDistance = Math.Max(worstDistance, frame.Distance);
                if (frame.Kind == LocateKind.Inside) inside++; else nearest++;
            }
            else if (frame.State == TrackState.OutsideMap)
            {
                outside++;
            }
        }

        var overlay = Readout.Lines(session);
        overlay.Add($"replay   {positions.Count} positions, {session.Trail.Count(p => p.HasValue)} trail points");

        var renderer = new MapRenderer(map);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            renderer.Render(dc, new Size(width, height), session, view, Readout.Message(session.Current), overlay);

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
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine(c, $"replay: {positions.Count} positions → {tracking} tracking ({inside} inside a polygon, {nearest} nearest-within-reach), {outside} outside the map");
        sb.AppendLine(c, $"layers visited (ticks): {string.Join(", ", layersSeen.Select(kv => $"#{kv.Key}: {kv.Value}"))}");
        sb.AppendLine(c, $"layer changes along the way: {Math.Max(0, runs.Count - 1)} (layer×ticks: {string.Join(" ", runs.Select(r => $"{r.Layer}×{r.Ticks}"))})");
        sb.AppendLine(c, $"largest distance to the located polygon: {worstDistance:F2} m");
        sb.AppendLine(c, $"orientation: {session.Transform.Orientation}; view: {(view.FitToWindow ? "fit" : $"follow, zoom {view.Zoom:F2}")}; image {width}x{height}");
        foreach (var line in overlay)
            sb.AppendLine("  " + line);
        sb.AppendLine($"wrote {Path.GetFullPath(pngPath)}");
        return sb.ToString();
    }

    /// <summary>Writes a synthetic walk between two bonfire markers as a replay file.</summary>
    public static string MakePath(MapData map, string outPath, int? fromEntity, int? toEntity, float step)
    {
        var bonfires = map.Markers.Where(m => m.IsBonfire && m.Layer >= 0).ToList();
        if (bonfires.Count < 2)
            throw new InvalidDataException("The map has fewer than two placed bonfire markers.");

        MapDocument.MarkerInfo Find(int id) =>
            bonfires.FirstOrDefault(b => b.EntityId == id) ?? throw new ArgumentException($"No placed bonfire with entity id {id}.");

        MapDocument.MarkerInfo from, to;
        List<Vector3> positions;
        bool alongNavmesh;
        if (fromEntity is { } fromId && toEntity is { } toId)
        {
            from = Find(fromId);
            to = Find(toId);
            positions = NavPath.Between(map, from.WorldPosition, to.WorldPosition, step, out alongNavmesh);
        }
        else
        {
            // No explicit pair: take the first two bonfires (by entity id) that are joined over the
            // navmesh, so the replay walks real floors, stairs and layers. Straight line as a last resort.
            var ordered = bonfires.OrderBy(b => b.EntityId).ToList();
            var starts = fromEntity is { } onlyFrom ? new List<MapDocument.MarkerInfo> { Find(onlyFrom) } : ordered;
            from = starts[0];
            to = ordered.First(b => b != from);
            positions = NavPath.Between(map, from.WorldPosition, to.WorldPosition, step, out alongNavmesh);
            foreach (var a in starts)
            {
                if (alongNavmesh)
                    break;
                foreach (var b in ordered.Where(b => b != a))
                {
                    var candidate = NavPath.Between(map, a.WorldPosition, b.WorldPosition, step, out bool connected);
                    if (!connected)
                        continue;
                    (from, to, positions, alongNavmesh) = (a, b, candidate, true);
                    break;
                }
            }
        }

        float length = 0;
        for (int i = 1; i < positions.Count; i++)
            length += Vector3.Distance(positions[i - 1], positions[i]);

        string header =
            $"Synthetic path between bonfire {from.Name} (entity {from.EntityId}) and {to.Name} (entity {to.EntityId}).\n" +
            (alongNavmesh ? "Follows connected navmesh polygons." : "Straight line: the two are not connected over the navmesh.") + "\n" +
            "Format: x,y,z per line (world coordinates, metres).";
        ReplayFile.Write(outPath, positions, header);

        return string.Create(CultureInfo.InvariantCulture,
            $"path: bonfire {from.EntityId} ({from.Name}) → bonfire {to.EntityId} ({to.Name}), {positions.Count} positions, {length:F1} m, " +
            $"{(alongNavmesh ? "along the navmesh" : "STRAIGHT LINE (not connected over the navmesh)")}\nwrote {Path.GetFullPath(outPath)}\n");
    }

    /// <summary>
    /// The live read check: attach (read-only), sample the position for a few seconds and
    /// compare it with the bonfire markers. Returns the process exit code:
    /// 0 read fine, 2 game not running, 3 access denied, 4 no character or unsupported, 5 values not finite or not stable.
    /// </summary>
    public static int Probe(MapData map, Orientation orientation, double seconds, TextWriter output)
    {
        var c = CultureInfo.InvariantCulture;
        using var source = new LiveGameSource();
        var session = new TrackingSession(map, orientation);
        var samples = new List<Vector3>();
        var statusCounts = new SortedDictionary<SourceStatus, int>();
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

        var end = DateTime.UtcNow.AddSeconds(seconds);
        TrackFrame frame = session.Current;
        while (DateTime.UtcNow < end)
        {
            last = source.Read();
            statusCounts[last.Status] = statusCounts.GetValueOrDefault(last.Status) + 1;
            if (last.Status == SourceStatus.Ok)
                samples.Add(last.Position);
            frame = session.Update(last);
            Thread.Sleep(SamplePump.IntervalMs);
        }

        output.WriteLine($"version: {last.GameVersion}");
        output.WriteLine($"readings over {seconds:F1} s: {string.Join(", ", statusCounts.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (samples.Count == 0)
        {
            output.WriteLine("RESULT: NO POSITION — attached, but no character is loaded (or the build is unsupported).");
            return 4;
        }

        bool finite = samples.All(v => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
        var min = new Vector3(samples.Min(v => v.X), samples.Min(v => v.Y), samples.Min(v => v.Z));
        var max = new Vector3(samples.Max(v => v.X), samples.Max(v => v.Y), samples.Max(v => v.Z));
        float spread = (max - min).Length();
        var p = samples[^1];
        output.WriteLine($"position: x {p.X:F3}  y {p.Y:F3}  z {p.Z:F3}   ({samples.Count} samples)");
        output.WriteLine($"finite: {finite}; spread while sampling: {spread:F4} m (x {max.X - min.X:F4}, y {max.Y - min.Y:F4}, z {max.Z - min.Z:F4})");

        foreach (var line in Readout.Lines(session))
            output.WriteLine("  " + line);

        var nearest = map.Markers.Where(m => m.IsBonfire)
            .Select(m => (Marker: m, Dist: Vector3.Distance(m.WorldPosition, p),
                          Horizontal: MathF.Sqrt(MathF.Pow(m.World[0] - p.X, 2) + MathF.Pow(m.World[2] - p.Z, 2))))
            .OrderBy(x => x.Dist).ToList();
        foreach (var n in nearest)
            output.WriteLine($"  bonfire {n.Marker.EntityId} {n.Marker.Name} at ({n.Marker.World[0]:F2}, {n.Marker.World[1]:F2}, {n.Marker.World[2]:F2}): {n.Dist:F2} m (horizontal {n.Horizontal:F2} m)");
        if (nearest.Count > 0)
            output.WriteLine($"nearest bonfire marker: {nearest[0].Dist:F2} m → {(nearest[0].Dist <= 3.0f ? "within 3 m (same coordinate space as the map)" : "NOT within 3 m (either not at a bonfire, or the spaces differ)")}");

        bool stable = spread <= 0.05f;
        output.WriteLine(finite && stable
            ? "RESULT: OK — position read, finite and stable."
            : $"RESULT: CHECK — finite={finite}, stable={stable} (expected a spread under 0.05 m while standing still).");
        return finite && stable ? 0 : 5;
    }
}
