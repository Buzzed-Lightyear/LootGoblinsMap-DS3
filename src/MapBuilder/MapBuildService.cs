using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MapBuilder.Archive;
using MapBuilder.Checks;
using MapBuilder.GameData;
using MapBuilder.Mods;
using MapBuilder.Pipeline;
using MapCore;

namespace MapBuilder;

/// <summary>Where a build is: which step, which map, how far along.</summary>
public readonly record struct BuildProgress(string Stage, string MapId, int Done, int Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>What a build produced.</summary>
public sealed class BuildSummary
{
    public List<MapCheckResult> Results = new();
    public List<MapOverlap> Overlaps = new();
    public List<string> Archives = new();
    public int MapsFound, MapsWithNavmesh, DlcMapsFound;
    public bool DlcArchivesPresent;
    public string GameDataProblem;
    public string ReportPath, IndexPath;
    public TimeSpan Elapsed;

    public int Built => Results.Count(r => r.Built);
    public int Passed => Results.Count(r => r.Passed);
}

/// <summary>
/// Builds the maps from a game install: finds the maps in the archives, reads each one, names
/// its markers from the game's own params and text, and writes one folder per map plus
/// index.json and a report. Used by the command-line builder and by the app's first run.
/// The game folder is only ever opened for reading.
/// </summary>
public static class MapBuildService
{
    private static bool _encodingsRegistered;

    /// <param name="onlyMaps">Null for every map, else the map ids to build.</param>
    /// <param name="extract">False to reuse the files already extracted into the work folder.</param>
    public static BuildSummary BuildAll(Config cfg, ISet<string> onlyMaps, bool extract, TextWriter log, IProgress<BuildProgress> progress = null)
    {
        if (!_encodingsRegistered)
        {
            // Shift-JIS support for the vendored SoulsFormats readers.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _encodingsRegistered = true;
        }

        var clock = Stopwatch.StartNew();
        var summary = new BuildSummary();
        string gameDataDir = cfg.ResolveGameDataDir();
        var excluded = (cfg.ExcludeArchives ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        progress?.Report(new BuildProgress("Opening the game archives", null, 0, 1));
        using var archives = ArchiveSet.OpenDarkSouls3(gameDataDir, excluded);
        summary.Archives = archives.ArchiveNames.ToList();
        summary.DlcArchivesPresent = summary.Archives.Any(a => a.StartsWith("DLC", StringComparison.OrdinalIgnoreCase));

        var discovered = MapDiscovery.Find(archives);
        summary.MapsFound = discovered.Count;
        summary.MapsWithNavmesh = discovered.Count(m => m.HasNavmesh);
        summary.DlcMapsFound = discovered.Count(m => m.Dlc);
        PrintDiscovery(discovered, archives, excluded, log);
        if (onlyMaps != null)
            foreach (string id in onlyMaps.Where(id => discovered.All(d => !d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                log.WriteLine($"WARNING: map '{id}' was asked for but is not in the archives.");

        // Game data shared by all maps: text and params. If it cannot be read the maps are still built, unnamed.
        progress?.Report(new BuildProgress("Reading item and place names", null, 0, 1));
        ItemResolver resolver = null;
        try
        {
            var text = GameText.Load(archives, cfg.Language, log);
            foreach (string source in text.Sources)
                log.WriteLine($"  text from {source}");
            var regulation = Regulation.Load(gameDataDir);
            resolver = ItemResolver.Load(regulation, text, log);
        }
        catch (Exception ex)
        {
            summary.GameDataProblem = ex.Message;
            log.WriteLine($"WARNING: item and name data could not be read ({ex.Message}). Maps are built without names.");
        }

        var buildable = discovered.Where(d => d.HasNavmesh && (onlyMaps == null || onlyMaps.Contains(d.Id))).ToList();
        ModScanResult mods = extract ? ScanMods(cfg, buildable.SelectMany(d => Extractor.NeededPaths(d.Id)), log) : null;
        int steps = buildable.Count * 2, step = 0;

        var results = summary.Results;
        var facesByMap = new Dictionary<string, List<WorldFace>>();

        // 1. Read every map: navmesh and markers, with the markers named from the game data.
        var decoded = new List<(DiscoveredMap Map, MapBuildContext Context, List<WorldFace> Faces, List<Marker> Markers)>();
        foreach (var map in discovered)
        {
            var context = new MapBuildContext { Resolver = resolver, Source = map.MsbArchive, Dlc = map.Dlc };
            if (!map.HasNavmesh)
            {
                results.Add(new MapCheckResult
                {
                    MapId = map.Id, Name = resolver?.AreaName(map.Id), Source = map.MsbArchive, Dlc = map.Dlc,
                    Built = false, Problem = "no navmesh in the archives (map file only)",
                });
                continue;
            }
            if (onlyMaps != null && !onlyMaps.Contains(map.Id))
                continue;

            progress?.Report(new BuildProgress("Reading", map.Id, step++, steps));
            log.WriteLine();
            log.WriteLine($"=== {map.Id}{(map.Dlc ? " (DLC)" : "")}: read ===");
            try
            {
                if (extract)
                    Extractor.Extract(archives, cfg, map.Id, mods, log);
                var (faces, markers) = BuildPipeline.Decode(map.Id, Extractor.ExtractDir(cfg, map.Id), log);
                Markers.Resolve(markers, resolver);
                decoded.Add((map, context, faces, markers));
            }
            catch (Exception ex)
            {
                // One failing map is reported, not hidden, and the others still build.
                log.WriteLine($"  FAILED: {ex.Message}");
                results.Add(new MapCheckResult
                {
                    MapId = map.Id, Name = resolver?.AreaName(map.Id), Source = map.MsbArchive, Dlc = map.Dlc,
                    Built = false, Problem = ex.Message,
                });
            }
        }

        // 2. A marker is shown on the map whose floor it stands on. A few are defined in one map's
        //    file but stand on a neighbour's navmesh (the maps share one coordinate space).
        foreach (var home in decoded)
        {
            foreach (var marker in home.Markers.Where(m => !m.Unused && m.FromMap == null).ToList())
            {
                // Only a marker that is nowhere near its own map's floor is a candidate, and only real
                // areas take markers in (arenas reuse the same coordinates by coincidence).
                if (Markers.DistanceToMesh(marker.World, home.Faces) <= 8f)
                    continue;
                var better = decoded
                    .Where(other => other.Map.Id != home.Map.Id && (resolver?.BonfiresListedFor(other.Map.Id) ?? 0) > 0)
                    .Select(other => (Other: other, Distance: Markers.DistanceToMesh(marker.World, other.Faces)))
                    .Where(x => x.Distance <= 2f)
                    .OrderBy(x => x.Distance)
                    .FirstOrDefault();
                if (better.Other.Markers == null)
                    continue;
                home.Markers.Remove(marker);
                marker.FromMap = home.Map.Id;
                better.Other.Markers.Add(marker);
                log.WriteLine($"Marker {marker.Type} {marker.Name} (entity {marker.EntityId}) is defined in {home.Map.Id} but stands on {better.Other.Map.Id}; shown there.");
            }
        }

        // 3. Build and check each map.
        foreach (var (map, context, faces, markers) in decoded)
        {
            progress?.Report(new BuildProgress("Drawing", map.Id, step++, steps));
            log.WriteLine();
            log.WriteLine($"=== {map.Id}{(map.Dlc ? " (DLC)" : "")}: build ===");
            try
            {
                var build = BuildPipeline.Export(cfg, map.Id, faces, markers, context, log);
                facesByMap[map.Id] = build.Faces;
                results.Add(Acceptance.Run(build, context, log));
            }
            catch (Exception ex)
            {
                log.WriteLine($"  FAILED: {ex.Message}");
                results.Add(new MapCheckResult
                {
                    MapId = map.Id, Name = resolver?.AreaName(map.Id), Source = map.MsbArchive, Dlc = map.Dlc,
                    Built = false, Problem = ex.Message,
                });
            }
        }
        results.Sort((a, b) => string.CompareOrdinal(a.MapId, b.MapId));

        // The world-space question, over every map built in this run.
        progress?.Report(new BuildProgress("Finishing", null, steps, Math.Max(steps, 1)));
        summary.Overlaps = OverlapAnalysis.Run(facesByMap);
        var names = results.ToDictionary(r => r.MapId, r => r.Name ?? "");
        string overlapText = OverlapAnalysis.Describe(summary.Overlaps, facesByMap.Count, names);

        string outRoot = cfg.ResolveOutDir();
        Directory.CreateDirectory(outRoot);
        summary.IndexPath = WriteIndex(cfg, outRoot, discovered, summary.Archives, summary.Overlaps.Count > 0, onlyMaps == null, log);

        var report = new StringBuilder();
        report.AppendLine("# Map build report");
        report.AppendLine();
        report.AppendLine($"Built {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC from {gameDataDir}. Archives: {string.Join(", ", summary.Archives)}. Language: {cfg.Language}.");
        if (!summary.DlcArchivesPresent)
            report.AppendLine("\nNo DLC archives were opened, so the DLC areas are not built.");
        if (summary.GameDataProblem != null)
            report.AppendLine($"\n**Item and name data could not be read:** {summary.GameDataProblem}");
        report.AppendLine();
        report.AppendLine("## Per-map table");
        report.AppendLine();
        report.Append(Acceptance.Table(results));
        report.AppendLine();
        report.AppendLine("## Do maps share space?");
        report.AppendLine();
        report.Append(overlapText);
        summary.ReportPath = Path.Combine(outRoot, onlyMaps == null ? "build_report.md" : "build_report_partial.md");
        File.WriteAllText(summary.ReportPath, report.ToString());

        log.WriteLine();
        log.Write(Acceptance.Table(results));
        log.WriteLine();
        log.Write(overlapText);
        summary.Elapsed = clock.Elapsed;
        log.WriteLine();
        log.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{summary.Built} maps built, {summary.Passed} pass every check, {results.Count(r => r.Built && !r.Passed)} built with a failing check, " +
            $"{results.Count(r => !r.Built)} not built, in {summary.Elapsed.TotalSeconds:F1} s. Report: {summary.ReportPath}"));
        return summary;
    }

    public static void PrintDiscovery(List<DiscoveredMap> maps, ArchiveSet archives, IReadOnlyCollection<string> excluded, TextWriter log)
    {
        log.WriteLine($"Archives: {string.Join(", ", archives.ArchiveNames)}" + (excluded.Count > 0 ? $" (left out on request: {string.Join(", ", excluded)})" : ""));
        if (!archives.ArchiveNames.Any(a => a.StartsWith("DLC", StringComparison.OrdinalIgnoreCase)))
            log.WriteLine("No DLC archives: the DLC areas will not be built. Everything else works without them.");
        log.WriteLine($"Maps found by probing the archives: {maps.Count} ({maps.Count(m => m.HasNavmesh)} with a navmesh, {maps.Count(m => m.Dlc)} from DLC archives)");
        foreach (var m in maps)
            log.WriteLine($"  {m.Id}  msb {m.MsbArchive}  navmesh {(m.HasNavmesh ? m.NavmeshArchive : "none")}{(m.Dlc ? "  (DLC)" : "")}");
    }

    /// <summary>index.json is rebuilt from what is on disk, so a partial run does not drop the other maps.</summary>
    private static string WriteIndex(Config cfg, string outRoot, List<DiscoveredMap> discovered, List<string> archives,
                                     bool mapsOverlap, bool fullRun, TextWriter log)
    {
        string indexPath = Path.Combine(outRoot, MapIndex.FileName);
        bool previousOverlap = false;
        if (!fullRun && File.Exists(indexPath))
        {
            try { previousOverlap = MapIndex.Load(indexPath).MapsOverlapInSpace; }
            catch (Exception) { /* a broken index is simply rewritten */ }
        }

        var index = new MapIndex
        {
            Language = cfg.Language,
            BuiltUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            MapsOverlapInSpace = fullRun ? mapsOverlap : mapsOverlap || previousOverlap,
            KnownMapIds = discovered.Select(d => d.Id).ToList(),
            Archives = archives,
        };
        foreach (var map in discovered)
        {
            string dir = Path.Combine(outRoot, map.Id);
            string json = Path.Combine(dir, MapDocument.FileName);
            if (!map.HasNavmesh || !File.Exists(json) || !File.Exists(Path.Combine(dir, NavPolyFile.FileName)))
                continue;
            var doc = MapDocument.Load(json);
            string acceptance = Path.Combine(dir, "acceptance.txt");
            index.Maps.Add(new MapIndex.Entry
            {
                Id = map.Id,
                Name = doc.Name,
                Directory = map.Id,
                Source = map.MsbArchive,
                Dlc = map.Dlc,
                Bounds = doc.Bounds,
                WidthPx = doc.Transform.WidthPx,
                HeightPx = doc.Transform.HeightPx,
                Layers = doc.Layers.Count,
                Polygons = doc.NavPolygonCount,
                Bonfires = doc.Markers.Count(m => m.IsBonfire && m.Layer >= 0),
                Treasures = doc.Markers.Count(m => !m.IsBonfire && !m.Unused),
                Built = true,
                ChecksPassed = File.Exists(acceptance) && !File.ReadAllText(acceptance).Contains("[FAIL]"),
            });
        }
        index.Save(indexPath);
        log.WriteLine($"Wrote {indexPath} ({index.Maps.Count} maps).");
        return indexPath;
    }

    public static ModScanResult ScanMods(Config cfg, IEnumerable<string> neededPaths, TextWriter log)
    {
        string gameDataDir = cfg.ResolveGameDataDir();
        var mods = ModScanner.Scan(gameDataDir, cfg.ModPath, neededPaths);

        log.WriteLine("Mod scan:");
        log.WriteLine($"  injection DLLs: {(mods.InjectionDlls.Count == 0 ? "none" : string.Join(", ", mods.InjectionDlls))}");
        log.WriteLine($"  mod config files: {(mods.ModConfigFiles.Count == 0 ? "none" : string.Join(", ", mods.ModConfigFiles))}");
        log.WriteLine($"  extra executables: {(mods.ExtraExecutables.Count == 0 ? "none" : string.Join(", ", mods.ExtraExecutables))}");
        log.WriteLine($"  Mod Engine: {(mods.ModEngineIni == null ? "not present" : $"{mods.ModEngineIni} (override dir: {mods.ModEngineOverrideDir ?? "?"})")}");
        log.WriteLine($"  loose game-data dirs: {(mods.LooseGameDataDirs.Count == 0 ? "none" : string.Join(", ", mods.LooseGameDataDirs))}");
        log.WriteLine(mods.AnyNeededFileOverridden
            ? "  OVERRIDES AFFECT THESE MAPS: " + string.Join("; ", mods.NeededFileOverrides.Select(kv => $"{kv.Key} ← {kv.Value}"))
            : "  no map file is overridden by a mod → building from the archives.");

        string workDir = cfg.ResolveWorkDir();
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, "modscan.json"), JsonSerializer.Serialize(mods, Config.JsonOpts));
        return mods;
    }
}
