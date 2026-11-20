using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MapCore;
using Tracker.Memory;
using Tracker.Overlay;
using Tracker.Sources;

namespace Tracker;

public static class Program
{
    private const string Usage = """
        Loot Goblins Map tracker

          Tracker --overlay [--replay <script.txt>]
              The overlay: a transparent, click-through minimap over the game window, with a
              full-map hotkey. With --replay it plays a script over the primary screen instead.

          Tracker [--replay <script.txt>]
              The debug window: an ordinary window with the map and a readout of what is read.

          Tracker --replay <script.txt> --png <out.png> [--view follow|fit|minimap|full] [--zoom <n>]
                  [--size <W>x<H>] [--layer <n>]
              No window: run the script through the tracker and save the rendered view.

          Tracker --make-path <script.txt> [--from-map <id>] [--to-map <id>] [--from <entity>] [--to <entity>] [--step <m>]
              Write a synthetic walk between two bonfires; with two adjacent maps it crosses from one into the other.

          Tracker --probe [--seconds <n>] [--flag <id>[,<id>…]] [--report <file>]
              No window: attach to the running game; read position, map id and flags; compare with the map data.

          Tracker --overlay-check [--report <file>]
              No window: put the overlay over the game's client area and read back where it is.

          Common options: --config <config.json>, --settings <settings.json>, --log <live_checks.log>,
                          --exit-after <seconds> (window modes; prints CPU and memory use on exit).

        The maps come from the builder's output folder named in config.json (outDir).
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        // Numbers are always written and parsed the same way, whatever the Windows region settings.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console attached */ }
        bool headless = HasFlag(args, "--png") || HasFlag(args, "--make-path") || HasFlag(args, "--probe") || HasFlag(args, "--help") ||
                        HasFlag(args, "--overlay-check");
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

        if (HasFlag(args, "--overlay-check"))
        {
            using var report = new StringWriter();
            int code = Headless.OverlayCheck(report);
            Console.Write(report.ToString());
            if (Option(args, "--report") is { } reportPath)
                File.WriteAllText(reportPath, report.ToString());
            return code;
        }

        string configPath = Option(args, "--config") ?? FindConfig()
            ?? throw new FileNotFoundException("config.json not found. Pass --config <path> or start the tracker from the repository folder.");
        configPath = Path.GetFullPath(configPath);
        var cfg = Config.Load(configPath);
        var maps = MapSet.Load(cfg.ResolveOutDir(), cfg.MapId);

        if (Option(args, "--make-path") is { } pathOut)
        {
            float step = Option(args, "--step") is { } s ? float.Parse(s, CultureInfo.InvariantCulture) : 0.5f;
            Console.Write(Headless.MakePath(maps, Option(args, "--from-map") ?? cfg.MapId, Option(args, "--to-map"), pathOut,
                                            IntOption(args, "--from"), IntOption(args, "--to"), step));
            return 0;
        }

        if (HasFlag(args, "--probe"))
        {
            double seconds = Option(args, "--seconds") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : 5;
            var flags = (Option(args, "--flag") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => int.Parse(f, CultureInfo.InvariantCulture)).ToList();
            using var report = new StringWriter();
            int code = Headless.Probe(maps, cfg, seconds, flags, report);
            Console.Write(report.ToString());
            if (Option(args, "--report") is { } reportPath)
                File.WriteAllText(reportPath, report.ToString());
            return code;
        }

        string? replayPath = Option(args, "--replay");
        string settingsPath = Option(args, "--settings") ?? TrackerCore.DefaultSettingsPath();
        string logPath = Option(args, "--log") ?? Path.Combine(cfg.ResolveWorkDir(), "live_checks.log");
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
            using var headlessCore = new TrackerCore(maps, cfg, configPath, source, label, settingsPath, logPath, startPump: false);
            Console.Write(Headless.RenderReplay(headlessCore, pngPath, view, zoom, width, height, IntOption(args, "--layer")));
            return 0;
        }

        var core = new TrackerCore(maps, cfg, configPath, source, label, settingsPath, logPath);
        foreach (string problem in core.Settings.Problems)
            Console.Error.WriteLine("settings: " + problem);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("ERROR: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Tracker error", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        if (Option(args, "--settings-png") is { } settingsPng)
        {
            // Self-check: a picture of the settings window, shown without taking the focus, then closed.
            var settingsWindow = new SettingsWindow(core.Settings, Array.Empty<string>(), _ => Array.Empty<string>()) { ShowActivated = false };
            settingsWindow.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)settingsWindow.Content;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var fs = File.Create(settingsPng))
                encoder.Save(fs);
            settingsWindow.Close();
            core.Dispose();
            return 0;
        }

        double? exitAfter = Option(args, "--exit-after") is { } ea ? double.Parse(ea, CultureInfo.InvariantCulture) : null;
        var started = Stopwatch.StartNew();

        if (HasFlag(args, "--overlay"))
        {
            var overlay = new OverlayController(core);
            Console.WriteLine($"Overlay started. Full map {core.Settings.FullMapHotkey}, layers {core.Settings.LayerUpHotkey} / {core.Settings.LayerDownHotkey}, " +
                              $"settings {core.Settings.SettingsHotkey}, show/hide {core.Settings.ToggleOverlayHotkey}. Close this console window or press Ctrl+C to quit.");
            Console.WriteLine($"Settings: {settingsPath}");
            Console.WriteLine($"Test log: {logPath}");
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

        var window = new MainWindow(core);
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
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cost: ran {wall:F1} s, CPU time {cpu:F2} s ({100 * cpu / wall / Environment.ProcessorCount:F1} % of {Environment.ProcessorCount} logical processors, {100 * cpu / wall:F0} % of one), working set {self.WorkingSet64 / 1048576.0:F0} MB, peak {self.PeakWorkingSet64 / 1048576.0:F0} MB{drawn}"));
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
