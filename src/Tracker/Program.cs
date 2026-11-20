using System.Globalization;
using System.IO;
using System.Windows;
using MapCore;
using Tracker.Memory;
using Tracker.Rendering;
using Tracker.Sources;

namespace Tracker;

public static class Program
{
    private const string Usage = """
        Loot Goblins Map tracker — High Wall of Lothric

          Tracker [--config <config.json>]
              Live window: reads the player position from the running game (read-only).

          Tracker --replay <positions.txt> [--config <config.json>]
              Window that plays back recorded or synthetic positions instead of the game.

          Tracker --replay <positions.txt> --png <out.png> [--view follow|fit] [--zoom <n>] [--size <W>x<H>]
              No window: run the positions through the tracker and save the rendered view.

          Tracker --make-path <positions.txt> [--from <bonfire entity id>] [--to <bonfire entity id>] [--step <metres>]
              Write a synthetic walk between two bonfire markers as a replay file.

          Tracker --probe [--seconds <n>] [--report <file>]
              No window: attach to the running game, sample the position, compare with the bonfire markers.

          Add --window-png <out.png> [--after-ticks <n>] to either window mode to save a picture of
          the window after n ticks (25 per second) and close it again; used as a self-check.

        The map comes from the builder's output folder named in config.json (outDir/mapId).
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        // Numbers are always written and parsed the same way, whatever the Windows region settings.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console attached */ }
        bool headless = HasFlag(args, "--png") || HasFlag(args, "--make-path") || HasFlag(args, "--probe") || HasFlag(args, "--help");
        try
        {
            return Run(args, headless);
        }
        catch (Exception ex)
        {
            Fail(headless, ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args, bool headless)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        // Hard rule: the tracker never runs elevated.
        if (Elevation.IsCurrentProcessElevated())
        {
            Fail(headless, "The tracker is running as administrator and will not continue. Start it from a normal (non-elevated) prompt.");
            return 3;
        }

        string configPath = Option(args, "--config") ?? FindConfig()
            ?? throw new FileNotFoundException("config.json not found. Pass --config <path> or start the tracker from the repository folder.");
        configPath = Path.GetFullPath(configPath);
        var cfg = Config.Load(configPath);
        string mapDir = Path.Combine(cfg.ResolveOutDir(), cfg.MapId);
        var map = MapData.Load(mapDir);
        var orientation = cfg.Orientation;

        if (Option(args, "--make-path") is { } pathOut)
        {
            float step = Option(args, "--step") is { } s ? float.Parse(s, CultureInfo.InvariantCulture) : 0.5f;
            Console.Write(Headless.MakePath(map, pathOut, IntOption(args, "--from"), IntOption(args, "--to"), step));
            return 0;
        }

        if (HasFlag(args, "--probe"))
        {
            double seconds = Option(args, "--seconds") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : 5;
            using var report = new StringWriter();
            int code = Headless.Probe(map, orientation, seconds, report);
            Console.Write(report.ToString());
            if (Option(args, "--report") is { } reportPath)
                File.WriteAllText(reportPath, report.ToString());
            return code;
        }

        string? replayPath = Option(args, "--replay");
        if (Option(args, "--png") is { } pngPath)
        {
            if (replayPath == null)
                throw new ArgumentException("--png needs --replay <positions.txt>.");
            var view = new ViewState
            {
                FitToWindow = string.Equals(Option(args, "--view"), "fit", StringComparison.OrdinalIgnoreCase),
                Zoom = Option(args, "--zoom") is { } z ? double.Parse(z, CultureInfo.InvariantCulture) : 1.5,
            };
            (int width, int height) = ParseSize(Option(args, "--size") ?? "1600x1000");
            Console.Write(Headless.RenderReplay(map, orientation, ReplayFile.Read(replayPath), pngPath, view, width, height));
            return 0;
        }

        IPositionSource source = replayPath != null
            ? new ReplaySource(ReplayFile.Read(replayPath))
            : new LiveGameSource();
        string label = replayPath != null ? "replay: " + Path.GetFileName(replayPath) : "live";

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Tracker error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var window = new MainWindow(map, orientation, source, configPath, label);
        if (Option(args, "--window-png") is { } windowPng)
        {
            int ticks = Option(args, "--after-ticks") is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 40;
            window.SaveSnapshotAndClose(windowPng, ticks);
        }
        return app.Run(window);
    }

    /// <summary>config.json in the current folder, or in a parent of the program's folder (the repository root).</summary>
    private static string? FindConfig()
    {
        const string name = "config.json";
        if (File.Exists(name))
            return name;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static (int, int) ParseSize(string text)
    {
        var parts = text.ToLowerInvariant().Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) ||
            w is < 100 or > 8000 || h is < 100 or > 8000)
            throw new ArgumentException($"--size expects <W>x<H> between 100 and 8000, got '{text}'.");
        return (w, h);
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    private static int? IntOption(string[] args, string name) =>
        Option(args, name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;

    private static void Fail(bool headless, string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        if (!headless)
            MessageBox.Show(message, "Loot Goblins Map tracker", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
