using System.Text;
using MapBuilder.Pipeline;

namespace MapBuilder.Checks;

/// <summary>Runs the machine-checkable acceptance checks from the brief and writes a report.</summary>
public static class Acceptance
{
    public static string Run(BuildResult build, TextWriter log)
    {
        var sb = new StringBuilder();
        bool allPass = true;

        // Check 2: non-trivial polygon count, plausible bounds.
        var b = build.Bounds;
        float extentX = b.MaxX - b.MinX, extentZ = b.MaxZ - b.MinZ;
        bool c2 = build.Faces.Count >= 500 &&
                  extentX is > 50 and < 5000 &&
                  extentZ is > 50 and < 5000;
        allPass &= c2;
        sb.AppendLine($"[{(c2 ? "PASS" : "FAIL")}] Check 2 — navmesh decodes non-trivially: {build.Faces.Count} polygons; " +
                      $"extent {extentX:F0} m x {extentZ:F0} m (height span {b.MinY:F1}..{b.MaxY:F1} m).");

        // Check 3: >= 90% of markers within 2 m horizontally of a polygon on their layer.
        var placed = build.MarkerList;
        int within = placed.Count(m => m.Layer >= 0 && m.DistToLayerPolyM <= 2.0f);
        double pct = placed.Count == 0 ? 0 : 100.0 * within / placed.Count;
        bool c3 = pct >= 90.0;
        allPass &= c3;
        sb.AppendLine($"[{(c3 ? "PASS" : "FAIL")}] Check 3 — markers near their layer's polygons: {within}/{placed.Count} ({pct:F1}%) within 2 m.");
        foreach (var m in placed.Where(m => m.Layer < 0 || m.DistToLayerPolyM > 2.0f))
        {
            string d = m.Layer < 0 ? "no layer assigned" : $"{m.DistToLayerPolyM:F2} m";
            sb.AppendLine($"         outlier: {m.Type} {m.Name} at ({m.World.X:F1}, {m.World.Y:F1}, {m.World.Z:F1}) — {d}");
        }

        // Check 4: at least three bonfires.
        int bonfires = placed.Count(m => m.Type == "bonfire");
        bool c4 = bonfires >= 3;
        allPass &= c4;
        sb.AppendLine($"[{(c4 ? "PASS" : "FAIL")}] Check 4 — bonfires found: {bonfires} (expected ≥ 3).");

        // Check 5: round-trip is covered by unit tests; also spot-check here at runtime.
        var t = build.Transform;
        bool c5 = true;
        foreach (var m in placed)
        {
            var (px, py) = t.ToPixel(m.World);
            var (wx, wz) = t.ToWorld(px, py);
            if (Math.Abs(wx - m.World.X) > 0.01 || Math.Abs(wz - m.World.Z) > 0.01)
            {
                c5 = false;
                break;
            }
        }
        allPass &= c5;
        sb.AppendLine($"[{(c5 ? "PASS" : "FAIL")}] Check 5 — world→pixel→world round-trip within 1 cm for all markers (full coverage in unit tests).");

        sb.AppendLine(allPass
            ? "All machine-checkable acceptance checks passed. (Checks 1, 6, 7 are verified outside this command: game-listing diff, visual PNG review, clean build/test.)"
            : "SOME CHECKS FAILED — see above.");

        string report = sb.ToString();
        log.Write(report);
        File.WriteAllText(Path.Combine(build.OutDir, "acceptance.txt"), report);
        return report;
    }
}
