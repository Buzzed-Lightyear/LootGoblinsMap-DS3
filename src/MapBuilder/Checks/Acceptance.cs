using System.Globalization;
using System.Text;
using MapBuilder.GameData;
using MapBuilder.Pipeline;
using MapCore;

namespace MapBuilder.Checks;

/// <summary>One named check on one map.</summary>
public sealed record CheckOutcome(string Name, bool Passed, string Detail);

/// <summary>The acceptance result of one map: a row of the per-map table.</summary>
public sealed class MapCheckResult
{
    public string MapId;
    public string Name;
    public string Source;
    public bool Dlc;
    public bool Built;
    /// <summary>Why the map was not built (no navmesh, or the error that stopped it).</summary>
    public string Problem;

    public int Polygons, StrayPolygons, Layers, Seams;
    public float ExtentX, ExtentZ, MinY, MaxY;
    public int Bonfires, BonfiresNamed, BonfiresListed;
    public int Treasures, TreasuresResolved, TreasuresWithFlag;
    public int MarkersTotal, MarkersPlaced, MarkersNear;
    /// <summary>Treasure events left out because their item lot does not exist in the regulation.</summary>
    public int UnusedTreasures;
    /// <summary>Markers defined in another map's file that stand on this map's floor.</summary>
    public int MovedIn;
    public List<CheckOutcome> Checks = new();

    public bool Passed => Built && Checks.All(c => c.Passed);
}

/// <summary>Runs the machine-checkable acceptance checks on a built map.</summary>
public static class Acceptance
{
    public static MapCheckResult Run(BuildResult build, MapBuildContext context, TextWriter log)
    {
        var c = CultureInfo.InvariantCulture;
        var b = build.Bounds;
        // Unused treasure events (their item lot does not exist) are not markers: nothing can be picked up there.
        var unused = build.MarkerList.Where(m => m.Unused).ToList();
        var markers = build.MarkerList.Where(m => !m.Unused).ToList();
        var result = new MapCheckResult
        {
            MapId = build.MapId,
            Name = build.Name,
            Source = context?.Source,
            Dlc = context?.Dlc ?? false,
            Built = true,
            Polygons = build.Faces.Count,
            StrayPolygons = build.StrayPolygons,
            Layers = build.Layers.Count,
            Seams = build.Seams,
            ExtentX = b.MaxX - b.MinX,
            ExtentZ = b.MaxZ - b.MinZ,
            MinY = b.MinY,
            MaxY = b.MaxY,
        };

        // Navmesh decodes to something map-sized. Arenas are small, so the floor is low.
        bool navmesh = build.Faces.Count >= 20 &&
                       result.ExtentX is > 10 and < 5000 &&
                       result.ExtentZ is > 10 and < 5000;
        result.Checks.Add(new CheckOutcome("navmesh", navmesh, string.Create(c,
            $"{build.Faces.Count} polygons, {result.ExtentX:F0} × {result.ExtentZ:F0} m, height {b.MinY:F1}..{b.MaxY:F1} m")));

        // At least 90 % of the markers that sit on the map are within 2 m of a polygon on their layer.
        var bonfires = markers.Where(m => m.Type == MapDocument.BonfireType).ToList();
        var treasures = markers.Where(m => m.Type == MapDocument.TreasureType).ToList();
        int near = markers.Count(m => m.Layer >= 0 && m.DistToLayerPolyM <= 2.0f);
        double pct = markers.Count == 0 ? 100 : 100.0 * near / markers.Count;
        result.MarkersTotal = markers.Count;
        result.MarkersPlaced = markers.Count(m => m.Layer >= 0);
        result.MarkersNear = near;
        result.Checks.Add(new CheckOutcome("markers", pct >= 90.0, string.Create(c,
            $"{near}/{markers.Count} within 2 m ({pct:F0} %)")));

        // A bonfire marker where the game lists a bonfire for the area.
        result.Bonfires = bonfires.Count(m => m.Layer >= 0);
        result.BonfiresNamed = bonfires.Count(m => m.Layer >= 0 && m.DisplayName != null);
        result.BonfiresListed = context?.Resolver?.BonfiresListedFor(build.MapId) ?? -1;
        bool bonfireOk = result.BonfiresListed <= 0 || result.Bonfires >= 1;
        result.Checks.Add(new CheckOutcome("bonfire", bonfireOk,
            result.BonfiresListed < 0 ? $"{result.Bonfires} placed (game table not loaded)"
            : result.BonfiresListed == 0 ? $"{result.Bonfires} placed, the game lists none for this map"
            : $"{result.Bonfires} placed, {result.BonfiresNamed} named, the game lists {result.BonfiresListed}"));

        // World → pixel → world round trip within 1 cm (full coverage in unit tests).
        var t = build.Transform;
        bool roundTrip = true;
        foreach (var m in markers)
        {
            var (px, py) = t.ToPixel(m.World);
            var (wx, wz) = t.ToWorld(px, py);
            if (Math.Abs(wx - m.World.X) > 0.01 || Math.Abs(wz - m.World.Z) > 0.01)
            {
                roundTrip = false;
                break;
            }
        }
        result.Checks.Add(new CheckOutcome("round trip", roundTrip, "world→pixel→world within 1 cm"));

        // No layer lies over itself.
        result.Checks.Add(new CheckOutcome("layers", build.SelfOverlaps == 0,
            build.SelfOverlaps == 0 ? $"{build.Layers.Count} layers, none lies over itself" : $"{build.SelfOverlaps} stacked pairs within a layer"));

        result.UnusedTreasures = unused.Count;
        result.MovedIn = markers.Count(m => m.FromMap != null);
        result.Treasures = treasures.Count;
        result.TreasuresResolved = treasures.Count(m => !m.Unresolved);
        result.TreasuresWithFlag = treasures.Count(m => m.Flag > 0);

        var sb = new StringBuilder();
        foreach (var check in result.Checks)
            sb.AppendLine($"  [{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
        sb.AppendLine(string.Create(c, $"  treasures: {result.Treasures}, {result.TreasuresResolved} fully resolved, {result.TreasuresWithFlag} with a pickup flag; " +
                                       $"{result.UnusedTreasures} unused treasure events left out; {result.MovedIn} markers taken over from a neighbouring map"));
        foreach (var m in markers.Where(m => m.Layer < 0 || m.DistToLayerPolyM > 2.0f))
        {
            string where = m.Layer < 0 ? "no polygon within reach" : string.Create(c, $"{m.DistToLayerPolyM:F1} m from its layer");
            sb.AppendLine(string.Create(c, $"    not near the navmesh: {m.Type} {m.Name} (entity {m.EntityId}) at ({m.World.X:F1}, {m.World.Y:F1}, {m.World.Z:F1}): {where}"));
        }
        foreach (var m in markers.Where(m => m.FromMap != null))
            sb.AppendLine($"    taken over from {m.FromMap}: {m.Type} {m.Name} (entity {m.EntityId})");
        foreach (var m in build.MarkerList.Where(m => m.Unresolved))
            sb.AppendLine($"    unresolved: {m.Type} {m.Name} (entity {m.EntityId}): {string.Join("; ", m.Notes)}");
        string report = sb.ToString();
        log.Write(report);
        File.WriteAllText(Path.Combine(build.OutDir, "acceptance.txt"), report);
        return result;
    }

    /// <summary>The per-map table as Markdown.</summary>
    public static string Table(IEnumerable<MapCheckResult> results)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("| Map | Area | From | Polygons | Layers | Size (m) | Bonfires | Treasures (resolved) | Unused events | Markers within 2 m | Checks |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            if (!r.Built)
            {
                sb.AppendLine($"| {r.MapId} | {r.Name ?? "—"} | {r.Source ?? "—"} | — | — | — | — | — | — | — | not built: {r.Problem} |");
                continue;
            }
            string failed = string.Join("; ", r.Checks.Where(k => !k.Passed).Select(k => $"{k.Name}: {k.Detail}"));
            sb.AppendLine(string.Create(c,
                $"| {r.MapId} | {r.Name ?? "(no name)"} | {r.Source}{(r.Dlc ? " (DLC)" : "")} | {r.Polygons}{(r.StrayPolygons > 0 ? $" ({r.StrayPolygons} stray)" : "")} | {r.Layers} | " +
                $"{r.ExtentX:F0} × {r.ExtentZ:F0} | {r.Bonfires}{(r.BonfiresListed >= 0 ? $" of {r.BonfiresListed} listed" : "")}, {r.BonfiresNamed} named | " +
                $"{r.Treasures} ({r.TreasuresResolved}) | {r.UnusedTreasures} | {r.MarkersNear}/{r.MarkersTotal} | " +
                $"{(r.Passed ? "pass" : "FAIL — " + failed)} |"));
        }
        return sb.ToString();
    }
}
