using System.Globalization;
using System.Text;
using MapBuilder.Archive;
using MapBuilder.Pipeline;
using MapCore;

namespace MapBuilder;

/// <summary>The builder's command line, kept for development. The packaged app calls <see cref="MapBuildService"/> directly.</summary>
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
        string wanted = OptionValue(args, "--map") ?? cfg.Maps ?? "all";
        ISet<string> only = string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        switch (command)
        {
            case "scan":
                MapBuildService.ScanMods(cfg, Array.Empty<string>(), log);
                return 0;

            case "list":
            {
                var excluded = (cfg.ExcludeArchives ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                using var archives = ArchiveSet.OpenDarkSouls3(cfg.ResolveGameDataDir(), excluded);
                MapBuildService.PrintDiscovery(MapDiscovery.Find(archives), archives, excluded, log);
                return 0;
            }

            // "build" and "check" are kept from phase 1/2: the same as "all" without reading the map files again.
            case "build":
            case "check":
                MapBuildService.BuildAll(cfg, only, extract: false, log);
                return 0;

            case "all":
                MapBuildService.BuildAll(cfg, only, extract: !args.Contains("--no-extract"), log);
                return 0;

            default:
                Console.Error.WriteLine(Usage);
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
}
