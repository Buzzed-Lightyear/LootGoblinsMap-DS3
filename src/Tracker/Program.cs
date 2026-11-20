using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MapBuilder;
using MapCore;
using Tracker.App;
using Tracker.Memory;
using Tracker.Overlay;
using Tracker.Rendering;
using Tracker.Sources;

namespace Tracker;

public static class Program
{
    private const string Usage = """
        Loot Goblins Map

          LootGoblinsMap
              The app: finds the game, builds the maps on first run, starts the overlay.

          LootGoblinsMap --version
          LootGoblinsMap --build-maps [--game <folder>]
              No window: find the game (through Steam unless --game is given), build every map
              into the data folder and print how long it took.

          LootGoblinsMap --overlay [--replay <script.txt>]
              The overlay on its own. With --replay it plays a script over the primary screen.
          LootGoblinsMap --debug [--replay <script.txt>]
              The debug window: an ordinary window with the map and a readout of what is read.

          LootGoblinsMap --make-demo
              Write a made-up pair of maps and three replay scripts into the data folder, for
              trying the app without the game.
          LootGoblinsMap --replay <script.txt> --png <out.png> [--view follow|fit|minimap|full]
                  [--zoom <n>] [--size <W>x<H>] [--layer <n>] [--theme <name>] [--banner <text>] [--clean]
              No window: run the script through the tracker and save the rendered view.
          LootGoblinsMap --make-path <script.txt> [--from-map <id>] [--to-map <id>] [--from <entity>] [--to <entity>] [--step <m>]
              Write a walk between two bonfires; with two adjacent maps it crosses from one into the other.

          LootGoblinsMap --probe [--seconds <n>] [--flag <id>[,<id>…]] [--report <file>]
              No window: attach to the running game; read position, map id and flags; compare with the map data.
          LootGoblinsMap --overlay-check [--report <file>]
              No window: put the overlay over the game's client area and read back where it is.
          LootGoblinsMap --export-feedback [<folder>]
              No window: write the feedback zip (logs, settings, version; no maps) to the folder, or the desktop.

          Common options:
            --data <folder>      data folder (default: %LocalAppData%\LootGoblinsMap, or LOOTGOBLINSMAP_DATA)
            --config <file>      development setup: maps and cache where a repository config.json says
            --maps <folder>      read the maps from this folder
            --settings <file>, --log <file>, --out <file> (also write what is printed to this file)
            --exit-after <seconds>   window modes; prints CPU and memory use on exit

        This is a windowed program: from a console, add "| more" or --out <file> to see what it prints.
        """;

    private static TextWriter _out = Console.Out;
    private static StringWriter? _copy;

    [STAThread]
    public static int Main(string[] args)
    {
        // Numbers are always written and parsed the same way, whatever the Windows region settings.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console attached */ }
        bool headless = new[] { "--png", "--make-path", "--probe", "--help", "--overlay-check", "--version", "--build-maps", "--make-demo", "--export-feedback" }
            .Any(flag => HasFlag(args, flag));

        string? outPath = Option(args, "--out");
        if (outPath != null)
        {
            _copy = new StringWriter();
            _out = new Tee(Console.Out, _copy);
        }
        try
        {
            return Run(args, headless);
        }
        catch (Exception ex)
        {
            Fail(headless, ex.Message);
            return 1;
        }
        finally
        {
            if (outPath != null && _copy != null)
            {
                try { File.WriteAllText(outPath, _copy.ToString()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static int Run(string[] args, bool headless)
    {
        if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
        {
            _out.WriteLine(Usage);
            return 0;
        }
        if (HasFlag(args, "--version"))
        {
            _out.WriteLine($"Loot Goblins Map {AppEnvironment.Version}");
            return 0;
        }

        // Hard rule: the app never runs elevated.
        if (Elevation.IsCurrentProcessElevated())
        {
            Fail(headless, "Loot Goblins Map is running as administrator and will not continue. Start it normally (not \"Run as administrator\").");
            return 3;
        }

        if (HasFlag(args, "--overlay-check"))
        {
            using var report = new StringWriter();
            int code = Headless.OverlayCheck(report);
            _out.Write(report.ToString());
            if (Option(args, "--report") is { } reportPath)
                File.WriteAllText(reportPath, report.ToString());
            return code;
        }

        string? configPath = Option(args, "--config");
        if (configPath != null)
            configPath = Path.GetFullPath(configPath);
        var env = AppEnvironment.Create(Option(args, "--data"), configPath, Option(args, "--maps"), Option(args, "--settings"), Option(args, "--log"));

        if (HasFlag(args, "--build-maps"))
            return BuildMaps(env, Option(args, "--game"));

        if (HasFlag(args, "--make-demo"))
        {
            _out.Write(DemoMap.Write(env));
            return 0;
        }

        if (HasFlag(args, "--export-feedback"))
        {
            string? folder = Option(args, "--export-feedback");
            if (folder != null)
                Directory.CreateDirectory(folder);
            _out.WriteLine("wrote " + env.ExportFeedback(folder));
            return 0;
        }

        bool wantsMaps = headless || HasFlag(args, "--overlay") || HasFlag(args, "--debug") || HasFlag(args, "--replay") ||
                         HasFlag(args, "--settings-png") || HasFlag(args, "--window-png");
        if (!wantsMaps)
            return RunLauncher(env, args);

        if (!env.MapsExist)
            throw new FileNotFoundException($"No maps in '{env.MapsDir}'. Start the app without options to build them, or run with --build-maps.");
        var cfg = env.BuildConfig();

        if (Option(args, "--make-path") is { } pathOut)
        {
            var maps = MapSet.Load(env.MapsDir, cfg.MapId);
            float step = Option(args, "--step") is { } s ? float.Parse(s, CultureInfo.InvariantCulture) : 0.5f;
            _out.Write(Headless.MakePath(maps, Option(args, "--from-map") ?? cfg.MapId, Option(args, "--to-map"), pathOut,
                                         IntOption(args, "--from"), IntOption(args, "--to"), step));
            return 0;
        }

        if (HasFlag(args, "--probe"))
        {
            var maps = MapSet.Load(env.MapsDir, cfg.MapId);
            double seconds = Option(args, "--seconds") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : 5;
            var flags = (Option(args, "--flag") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => int.Parse(f, CultureInfo.InvariantCulture)).ToList();
            using var report = new StringWriter();
            int code = Headless.Probe(maps, cfg, seconds, flags, report);
            _out.Write(report.ToString());
            if (Option(args, "--report") is { } reportPath)
                File.WriteAllText(reportPath, report.ToString());
            return code;
        }

        string? replayPath = Option(args, "--replay");
        IPositionSource source = replayPath != null
            ? new ReplaySource(ReplayScript.Read(replayPath))
            : new LiveGameSource();
        string label = replayPath != null ? "replay: " + Path.GetFileName(replayPath) : "live";

        if (Option(args, "--png") is { } pngPath)
        {
            if (replayPath == null)
                throw new ArgumentException("--png needs --replay <script.txt>.");
            string view = (Option(args, "--view") ?? "follow").ToLowerInvariant();
            if (view is not ("follow" or "fit" or "minimap" or "full"))
                throw new ArgumentException($"--view must be follow, fit, minimap or full, not '{view}'.");
            double zoom = Option(args, "--zoom") is { } z ? double.Parse(z, CultureInfo.InvariantCulture) : 1.5;
            (int width, int height) = ParseSize(Option(args, "--size") ?? (view is "minimap" or "full" ? "1920x1080" : "1600x1000"));
            using var headlessCore = new TrackerCore(env, source, label, startPump: false);
            ApplyTheme(headlessCore, env, args);
            _out.Write(Headless.RenderReplay(headlessCore, pngPath, view, zoom, width, height, IntOption(args, "--layer"),
                                             Option(args, "--banner"), readout: !HasFlag(args, "--clean")));
            _out.WriteLine($"theme {headlessCore.Renderer.Theme.Name}; version {AppEnvironment.Version}");
            return 0;
        }

        var core = new TrackerCore(env, source, label);
        ApplyTheme(core, env, args);
        foreach (string problem in core.Settings.Problems)
            Console.Error.WriteLine("settings: " + problem);

        var app = NewApplication(ShutdownMode.OnMainWindowClose);

        if (Option(args, "--settings-png") is { } settingsPng)
        {
            // Self-check: a picture of the settings window, shown without taking the focus, then closed.
            var settingsWindow = new SettingsWindow(core.Settings, Array.Empty<string>(), _ => Array.Empty<string>(), env) { ShowActivated = false };
            settingsWindow.Show();
            SaveContentPng(settingsWindow, settingsPng);
            settingsWindow.Close();
            core.Dispose();
            return 0;
        }

        double? exitAfter = Option(args, "--exit-after") is { } ea ? double.Parse(ea, CultureInfo.InvariantCulture) : null;
        var started = Stopwatch.StartNew();

        if (HasFlag(args, "--overlay"))
        {
            var overlay = new OverlayController(core, env);
            _out.WriteLine($"Overlay started. Full map {core.Settings.FullMapHotkey}, layers {core.Settings.LayerUpHotkey} / {core.Settings.LayerDownHotkey}, " +
                           $"settings {core.Settings.SettingsHotkey}, show/hide {core.Settings.ToggleOverlayHotkey}, mark for feedback {core.Settings.MarkFeedbackHotkey}.");
            _out.WriteLine($"Settings: {env.SettingsPath}");
            _out.WriteLine($"Test log: {env.LiveLogPath}");
            app.Startup += (_, _) =>
            {
                overlay.Start();
                foreach (string problem in overlay.HotkeyProblems)
                    Console.Error.WriteLine("hotkey: " + problem);
            };
            ExitAfter(app, exitAfter, () => overlay.Window.Close());
            int code = app.Run();
            ReportCost(started, exitAfter, overlay.Frames);
            return code;
        }

        var window = new MainWindow(core, env);
        if (Option(args, "--window-png") is { } windowPng)
        {
            int ticks = Option(args, "--after-ticks") is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 40;
            window.SaveSnapshotAndClose(windowPng, ticks);
        }
        ExitAfter(app, exitAfter, () => window.Close());
        int result = app.Run(window);
        ReportCost(started, exitAfter, null);
        return result;
    }

    /// <summary>The app as a player uses it: one window that sets everything up and starts the overlay.</summary>
    private static int RunLauncher(AppEnvironment env, string[] args)
    {
        var app = NewApplication(ShutdownMode.OnMainWindowClose);
        string? png = Option(args, "--launcher-png");
        var launcher = new LauncherWindow(env, firstRun: png == null);
        if (png != null)
        {
            // Self-check: a picture of the main window, shown without taking the focus, then closed.
            launcher.ShowActivated = false;
            launcher.Show();
            SaveContentPng(launcher, png);
            launcher.Close();
            return 0;
        }
        double? exitAfter = Option(args, "--exit-after") is { } ea ? double.Parse(ea, CultureInfo.InvariantCulture) : null;
        ExitAfter(app, exitAfter, () => launcher.Close());
        return app.Run(launcher);
    }

    /// <summary>
    /// The first run without a window: find the game, build every map into the data folder, and
    /// say what was found and how long it took. Exit code 0 built, 2 game not found, 5 nothing built.
    /// </summary>
    private static int BuildMaps(AppEnvironment env, string? gameOverride)
    {
        if (env.DevConfigPath == null)
        {
            string? game = gameOverride;
            string how = "given with --game";
            if (game == null && SteamLibrary.IsGameFolder(env.State.GamePath ?? ""))
                (game, how) = (env.State.GamePath, "remembered from an earlier run");
            if (game == null)
                (game, how) = (AppEnvironment.FindGameThroughSteam(), "found through Steam's library list");
            if (game == null || !SteamLibrary.IsGameFolder(game))
            {
                _out.WriteLine(game == null
                    ? "RESULT: game not found. Dark Souls III is not in any Steam library on this computer; pass --game <folder>."
                    : $"RESULT: '{game}' does not contain the game's data files.");
                return 2;
            }
            _out.WriteLine($"game: {game} ({how})");
            env.State.GamePath = game;
            env.SaveState();
        }

        _out.WriteLine($"data folder: {env.DataDir}");
        string lastStage = "";
        var progress = new InlineProgress(p =>
        {
            if (p.MapId == null && p.Stage != lastStage)
                _out.WriteLine("  " + (lastStage = p.Stage));
        });
        var log = new StringWriter();
        var summary = env.BuildMaps(progress, log);
        _out.WriteLine($"archives: {string.Join(", ", summary.Archives)}");
        _out.WriteLine($"maps found: {summary.MapsFound} ({summary.MapsWithNavmesh} with a navmesh, {summary.DlcMapsFound} from DLC archives)");
        if (!summary.DlcArchivesPresent)
            _out.WriteLine("No DLC archives were found: the DLC areas are not built. Everything else works without them.");
        if (summary.GameDataProblem != null)
            _out.WriteLine("WARNING: item and place names could not be read: " + summary.GameDataProblem);
        foreach (var r in summary.Results.Where(r => r.Built && !r.Passed))
            _out.WriteLine($"  check failed: {r.MapId}");
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"RESULT: built {summary.Built} maps ({summary.Passed} passed their checks) in {summary.Elapsed.TotalSeconds:F1} s"));
        _out.WriteLine($"build log: {Path.Combine(env.LogsDir, "last_build.log")}");
        return summary.Built > 0 ? 0 : 5;
    }

    /// <summary>Progress reported on the calling thread, in order (the stock class posts to the thread pool).</summary>
    private sealed class InlineProgress(Action<BuildProgress> onReport) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => onReport(value);
    }

    /// <summary>Writes to two places at once: the console and the copy kept for --out.</summary>
    private sealed class Tee(TextWriter first, TextWriter second) : TextWriter
    {
        public override System.Text.Encoding Encoding => first.Encoding;
        public override void Write(char value) { first.Write(value); second.Write(value); }
        public override void Write(string? value) { first.Write(value); second.Write(value); }
        public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
    }

    private static void ApplyTheme(TrackerCore core, AppEnvironment env, string[] args)
    {
        if (Option(args, "--theme") is { } theme)
            core.Renderer.Theme = Theme.Load(env.ThemesDir, theme);
    }

    private static Application NewApplication(ShutdownMode mode)
    {
        var app = new Application { ShutdownMode = mode };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("ERROR: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Loot Goblins Map", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        return app;
    }

    private static void SaveContentPng(Window window, string path)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        // A visual is drawn at its offset inside its parent, so the picture has to include the margins.
        double width = root.ActualWidth + root.Margin.Left + root.Margin.Right, height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var back = new System.Windows.Media.DrawingVisual();
        using (var dc = back.RenderOpen())
            dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, width, height));
        bitmap.Render(back);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void ExitAfter(Application app, double? seconds, Action close)
    {
        if (seconds is not { } s)
            return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(s) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            close();
        };
        app.Startup += (_, _) => timer.Start();
    }

    /// <summary>CPU and memory of this process over the run (for the cost check).</summary>
    private static void ReportCost(Stopwatch started, double? exitAfter, int? frames)
    {
        if (exitAfter == null)
            return;
        using var self = Process.GetCurrentProcess();
        double wall = started.Elapsed.TotalSeconds, cpu = self.TotalProcessorTime.TotalSeconds;
        string drawn = frames is { } f ? string.Create(CultureInfo.InvariantCulture, $", {f} frames drawn ({f / wall:F1} per second)") : "";
        _out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cost: ran {wall:F1} s, CPU time {cpu:F2} s ({100 * cpu / wall / Environment.ProcessorCount:F1} % of {Environment.ProcessorCount} logical processors, {100 * cpu / wall:F0} % of one), working set {self.WorkingSet64 / 1048576.0:F0} MB, peak {self.PeakWorkingSet64 / 1048576.0:F0} MB{drawn}"));
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
        _copy?.WriteLine("ERROR: " + message);
        if (!headless)
            MessageBox.Show(message, "Loot Goblins Map", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
