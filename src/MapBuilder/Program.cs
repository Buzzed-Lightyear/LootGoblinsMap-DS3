using System.Text;
using System.Text.Json;
using MapBuilder.Checks;
using MapCore;
using MapBuilder.Mods;
using MapBuilder.Pipeline;

namespace MapBuilder;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Shift-JIS support for the vendored SoulsFormats readers.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // MSB names are Japanese; make them survive a Windows console.
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* output is not a console */ }

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
        string command = args.FirstOrDefault() ?? "all";

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
                ScanMods(cfg, log);
                return 0;

            case "extract":
                Extractor.Extract(cfg, ScanMods(cfg, log), log);
                return 0;

            case "build":
            {
                string extractDir = Path.Combine(cfg.ResolveWorkDir(), "extract", cfg.MapId);
                BuildPipeline.Run(cfg, extractDir, log);
                return 0;
            }

            case "check":
            {
                string extractDir = Path.Combine(cfg.ResolveWorkDir(), "extract", cfg.MapId);
                var build = BuildPipeline.Run(cfg, extractDir, log);
                Acceptance.Run(build, log);
                return 0;
            }

            case "all":
            {
                var mods = ScanMods(cfg, log);
                var extracted = Extractor.Extract(cfg, mods, log);
                string extractDir = Path.GetDirectoryName(extracted[0].LocalPath);
                var build = BuildPipeline.Run(cfg, extractDir, log);
                Acceptance.Run(build, log);
                return 0;
            }

            default:
                Console.Error.WriteLine("usage: mapbuilder [scan|extract|build|check|all|snapshot] [--config <path>] [--game-path <path>]");
                return 2;
        }
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

    private static ModScanResult ScanMods(Config cfg, TextWriter log)
    {
        string gameDataDir = cfg.ResolveGameDataDir();
        var mods = ModScanner.Scan(gameDataDir, cfg.ModPath, Extractor.NeededPaths(cfg.MapId));

        log.WriteLine("Mod scan:");
        log.WriteLine($"  injection DLLs: {(mods.InjectionDlls.Count == 0 ? "none" : string.Join(", ", mods.InjectionDlls))}");
        log.WriteLine($"  mod config files: {(mods.ModConfigFiles.Count == 0 ? "none" : string.Join(", ", mods.ModConfigFiles))}");
        log.WriteLine($"  extra executables: {(mods.ExtraExecutables.Count == 0 ? "none" : string.Join(", ", mods.ExtraExecutables))}");
        log.WriteLine($"  Mod Engine: {(mods.ModEngineIni == null ? "not present" : $"{mods.ModEngineIni} (override dir: {mods.ModEngineOverrideDir ?? "?"})")}");
        log.WriteLine($"  loose game-data dirs: {(mods.LooseGameDataDirs.Count == 0 ? "none" : string.Join(", ", mods.LooseGameDataDirs))}");
        log.WriteLine(mods.AnyNeededFileOverridden
            ? "  OVERRIDES AFFECT THIS MAP: " + string.Join("; ", mods.NeededFileOverrides.Select(kv => $"{kv.Key} ← {kv.Value}"))
            : "  no file needed for this map is overridden by a mod → building from the vanilla archives.");

        string workDir = cfg.ResolveWorkDir();
        Directory.CreateDirectory(workDir);
        File.WriteAllText(Path.Combine(workDir, "modscan.json"), JsonSerializer.Serialize(mods, Config.JsonOpts));
        return mods;
    }
}
