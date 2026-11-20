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

internal static class Program
{
    private const string Usage = """
        usage: mapbuilder <command> [--config <path>] [--game-path <path>] [--map <id>[,<id>…]] [--no-extract]

          all        Find every map in the archives, extract, build and check each one,
                     write out/index.json and out/build_report.md.  (default)
          list       Only list the maps found in the archives.
          scan       Report how mods are installed.
          snapshot <dir> <outfile>   Listing of a folder (path, size, modified time).

          --map         Build only these maps (the others keep what they have; the index is rebuilt from disk).
          --no-extract  Reuse the files already in work/extract instead of reading the archives again.
        """;

    private static int Main(string[] args)
    {
        // Shift-JIS support for the vendored SoulsFormats readers.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // MSB names are Japanese; make them survive a Windows console.
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* output is not a console */ }
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "all";

        if (command == "snapshot")
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("usage: mapbuilder snapshot <dir> <outfile>");
                return 2;
            }
            Snapshot.Write(args[1], args[2]);
            Console.WriteLine($"Snapshot of {args[1]} written to {args[2]}");
            return 0;
        }

        var cfg = LoadConfig(args);
        var log = Console.Out;

        switch (command)
        {
            case "scan":
                ScanMods(cfg, Array.Empty<string>(), log);
                return 0;

            case "list":
            {
                using var archives = ArchiveSet.OpenDarkSouls3(cfg.ResolveGameDataDir());
                PrintDiscovery(MapDiscovery.Find(archives), archives, log);
                return 0;
            }

            // "build" and "check" are kept from phase 1/2: the same as "all" without reading the map files again.
            case "build":
            case "check":
                return BuildAll(cfg, args, log, extract: false);

            case "all":
                return BuildAll(cfg, args, log, extract: !args.Contains("--no-extract"));

            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private static void PrintDiscovery(List<DiscoveredMap> maps, ArchiveSet archives, TextWriter log)
    {
        log.WriteLine($"Archives: {string.Join(", ", archives.ArchiveNames)}");
        log.WriteLine($"Maps found by probing the archives: {maps.Count} ({maps.Count(m => m.HasNavmesh)} with a navmesh, {maps.Count(m => m.Dlc)} from DLC archives)");
        foreach (var m in maps)
            log.WriteLine($"  {m.Id}  msb {m.MsbArchive}  navmesh {(m.HasNavmesh ? m.NavmeshArchive : "none")}{(m.Dlc ? "  (DLC)" : "")}");
    }

    private static int BuildAll(Config cfg, string[] args, TextWriter log, bool extract)
    {
        string gameDataDir = cfg.ResolveGameDataDir();
        using var archives = ArchiveSet.OpenDarkSouls3(gameDataDir);
        var discovered = MapDiscovery.Find(archives);
        PrintDiscovery(discovered, archives, log);

        // Which maps to build this run.
        string wanted = OptionValue(args, "--map") ?? cfg.Maps ?? "all";
        var selected = string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected != null)
            foreach (string id in selected.Where(id => discovered.All(d => !d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                log.WriteLine($"WARNING: map '{id}' was asked for but is not in the archives.");

        // Game data shared by all maps: text and params. If it cannot be read the maps are still built, unnamed.
        ItemResolver resolver = null;
        string gameDataProblem = null;
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
            gameDataProblem = ex.Message;
            log.WriteLine($"WARNING: item and name data could not be read ({ex.Message}). Maps are built without names.");
        }

        var buildable = discovered.Where(d => d.HasNavmesh && (selected == null || selected.Contains(d.Id))).ToList();
        ModScanResult mods = extract ? ScanMods(cfg, buildable.SelectMany(d => Extractor.NeededPaths(d.Id)), log) : null;

        var results = new List<MapCheckResult>();
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
            if (selected != null && !selected.Contains(map.Id))
                continue;

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
        results = results.OrderBy(r => r.MapId, StringComparer.Ordinal).ToList();

        // The world-space question, over every map built in this run.
        var overlaps = OverlapAnalysis.Run(facesByMap);
        var names = results.ToDictionary(r => r.MapId, r => r.Name ?? "");
        string overlapText = OverlapAnalysis.Describe(overlaps, facesByMap.Count, names);

        string outRoot = cfg.ResolveOutDir();
        Directory.CreateDirectory(outRoot);
        WriteIndex(cfg, outRoot, discovered, overlaps.Count > 0, selected == null, log);

        var report = new StringBuilder();
        report.AppendLine("# Map build report");
        report.AppendLine();
        report.AppendLine($"Built {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC from {gameDataDir}. Archives: {string.Join(", ", archives.ArchiveNames)}. Language: {cfg.Language}.");
        if (gameDataProblem != null)
            report.AppendLine($"\n**Item and name data could not be read:** {gameDataProblem}");
        report.AppendLine();
        report.AppendLine("## Per-map table");
        report.AppendLine();
        report.Append(Acceptance.Table(results));
        report.AppendLine();
        report.AppendLine("## Do maps share space?");
        report.AppendLine();
        report.Append(overlapText);
        string reportPath = Path.Combine(outRoot, selected == null ? "build_report.md" : "build_report_partial.md");
        File.WriteAllText(reportPath, report.ToString());

        log.WriteLine();
        log.Write(Acceptance.Table(results));
        log.WriteLine();
        log.Write(overlapText);
        int built = results.Count(r => r.Built), passed = results.Count(r => r.Passed);
        log.WriteLine();
        log.WriteLine($"{built} maps built, {passed} pass every check, {results.Count(r => r.Built && !r.Passed)} built with a failing check, " +
                      $"{results.Count(r => !r.Built)} not built. Report: {reportPath}");
        return 0;
    }

    /// <summary>index.json is rebuilt from what is on disk, so a partial run does not drop the other maps.</summary>
    private static void WriteIndex(Config cfg, string outRoot, List<DiscoveredMap> discovered, bool mapsOverlap, bool fullRun, TextWriter log)
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
                Treasures = doc.Markers.Count(m => !m.IsBonfire),
                Built = true,
                ChecksPassed = File.Exists(acceptance) && !File.ReadAllText(acceptance).Contains("[FAIL]"),
            });
        }
        index.Save(indexPath);
        log.WriteLine($"Wrote {indexPath} ({index.Maps.Count} maps).");
    }

    private static Config LoadConfig(string[] args)
    {
        string configPath = OptionValue(args, "--config") ?? "config.json";
        Config cfg;
        if (File.Exists(configPath))
        {
            cfg = Config.Load(configPath);
        }
        else
        {
            cfg = new Config();
            Console.WriteLine($"(config file '{configPath}' not found; using defaults + CLI options)");
        }

        string gamePath = OptionValue(args, "--game-path");
        if (gamePath != null)
            cfg.GamePath = gamePath;
        return cfg;
    }

    private static string OptionValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static ModScanResult ScanMods(Config cfg, IEnumerable<string> neededPaths, TextWriter log)
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
