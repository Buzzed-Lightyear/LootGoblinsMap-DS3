using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using MapBuilder;
using MapCore;
using Microsoft.Win32;

namespace Tracker.App;

/// <summary>
/// Where the app keeps its things, and what it knows about itself. In the packaged app
/// everything lives in a per-user data folder: maps, cache, settings, logs. Nothing is written
/// next to the executable and nothing into the game folder. With <c>--config</c> (development)
/// maps and cache are where the repository's config.json says.
/// </summary>
internal sealed class AppEnvironment
{
    public string DataDir { get; private init; } = "";
    public string MapsDir { get; private init; } = "";
    public string CacheDir { get; private init; } = "";
    public string LogsDir { get; private init; } = "";
    public string SettingsPath { get; init; } = "";
    public string StatePath { get; private init; } = "";
    public string ThemesDir { get; private init; } = "";
    public string LiveLogPath { get; init; } = "";
    public string FeedbackLogPath { get; private init; } = "";

    /// <summary>The repository's config.json when started with --config; null in the packaged app.</summary>
    public string? DevConfigPath { get; private init; }
    public AppState State { get; private init; } = new();

    /// <summary>The version this build was stamped with (from the git tag in a release build).</summary>
    public static string Version
    {
        get
        {
            string? info = typeof(AppEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrEmpty(info) ? "0.0.0-dev" : info;
        }
    }

    /// <summary>The version without the source revision suffix.</summary>
    public static string ShortVersion => Version.Split('+')[0];

    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("LOOTGOBLINSMAP_DATA") is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LootGoblinsMap");

    /// <param name="dataDir">Null for the default: the per-user folder, or <c>appdata</c> in the work folder of a development setup.</param>
    public static AppEnvironment Create(string? dataDir, string? devConfigPath, string? mapsDirOverride,
                                        string? settingsPath = null, string? liveLogPath = null)
    {
        bool chosen = dataDir != null;
        if (dataDir == null && devConfigPath != null)
            dataDir = Path.Combine(Config.Load(devConfigPath).ResolveWorkDir(), "appdata");
        string data = Path.GetFullPath(dataDir ?? DefaultDataDir());
        Directory.CreateDirectory(data);
        string logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(logs);
        string statePath = Path.Combine(data, "app.json");

        string maps = Path.Combine(data, "maps"), cache = Path.Combine(data, "cache");
        if (devConfigPath != null)
        {
            var dev = Config.Load(devConfigPath);
            maps = dev.ResolveOutDir();
            cache = dev.ResolveWorkDir();
        }
        if (mapsDirOverride != null)
            maps = Path.GetFullPath(mapsDirOverride);

        var env = new AppEnvironment
        {
            DataDir = data, MapsDir = maps, CacheDir = cache, LogsDir = logs, StatePath = statePath,
            SettingsPath = settingsPath != null ? Path.GetFullPath(settingsPath) : Path.Combine(data, "settings.json"),
            ThemesDir = Path.Combine(data, "themes"),
            LiveLogPath = liveLogPath != null ? Path.GetFullPath(liveLogPath) : Path.Combine(logs, "live_checks.log"),
            FeedbackLogPath = Path.Combine(logs, "feedback.log"),
            DevConfigPath = devConfigPath,
            State = AppState.Load(statePath),
        };
        if (!chosen && settingsPath == null)
            env.MigrateOldSettings();
        return env;
    }

    /// <summary>Phase 3 kept the settings under the roaming profile; carry them over once.</summary>
    private void MigrateOldSettings()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LootGoblinsMap", "settings.json");
            if (!File.Exists(SettingsPath) && File.Exists(old))
                File.Copy(old, SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void SaveState() => State.Save(StatePath);

    /// <summary>The builder/tracker configuration for this run.</summary>
    public Config BuildConfig()
    {
        if (DevConfigPath != null)
        {
            var dev = Config.Load(DevConfigPath);
            if (MapsDir != dev.ResolveOutDir())
                dev.OutDir = MapsDir;
            return dev;
        }
        return new Config
        {
            GamePath = State.GamePath ?? "",
            OutDir = MapsDir,
            WorkDir = CacheDir,
            FlipX = State.FlipX, FlipZ = State.FlipZ, Rotation = State.Rotation,
            WriteLayerImages = false,       // the app draws the maps itself
            ProjectRoot = DataDir,
        };
    }

    public void SaveOrientation(Orientation orientation)
    {
        if (DevConfigPath != null)
        {
            ConfigFile.SaveOrientation(DevConfigPath, orientation);
            return;
        }
        var o = orientation.Normalized();
        (State.FlipX, State.FlipZ, State.Rotation) = (o.FlipX, o.FlipZ, o.Rotation);
        SaveState();
    }

    public bool MapsExist => File.Exists(Path.Combine(MapsDir, MapIndex.FileName));

    // ---- the game ------------------------------------------------------------------------

    /// <summary>
    /// Finds the game by reading where Steam is installed (registry, read-only) and Steam's
    /// own list of library folders. Returns null when it is not found that way.
    /// </summary>
    public static string? FindGameThroughSteam()
    {
        var roots = new List<string>();
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(key, writable: false);
                if (k?.GetValue(value) is string path && path.Length > 0)
                    roots.Add(path.Replace('/', '\\'));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return SteamLibrary.FindGame(roots);
    }

    /// <summary>True when the archives differ from the ones the maps were built from (a patch, or DLC added or removed).</summary>
    public bool ArchivesChangedSinceBuild(out string? currentStamp)
    {
        currentStamp = null;
        if (string.IsNullOrEmpty(State.GamePath) || !SteamLibrary.IsGameFolder(State.GamePath))
            return false;
        try
        {
            currentStamp = BuildStamp.Compute(State.GamePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return !string.IsNullOrEmpty(State.BuiltStamp) && State.BuiltStamp != currentStamp;
    }

    /// <summary>Builds every map into the maps folder and records what was built, from what, and how long it took.</summary>
    public BuildSummary BuildMaps(IProgress<BuildProgress>? progress, TextWriter log)
    {
        var cfg = BuildConfig();
        Directory.CreateDirectory(MapsDir);
        Directory.CreateDirectory(CacheDir);
        string stamp = BuildStamp.Compute(cfg.GamePath);
        var summary = MapBuildService.BuildAll(cfg, onlyMaps: null, extract: true, log, progress);
        State.BuiltStamp = stamp;
        State.BuiltUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        State.BuiltWithVersion = ShortVersion;
        State.BuildSeconds = Math.Round(summary.Elapsed.TotalSeconds, 1);
        State.MapsBuilt = summary.Built;
        State.DlcBuilt = summary.DlcArchivesPresent;
        SaveState();
        try
        {
            if (log is StringWriter text)
                File.WriteAllText(Path.Combine(LogsDir, "last_build.log"), text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return summary;
    }

    // ---- feedback ------------------------------------------------------------------------

    /// <summary>Appends a "something is wrong here" note.</summary>
    public void MarkFeedback(TrackingSession session)
    {
        try
        {
            File.AppendAllText(FeedbackLogPath, FeedbackNote.Format(session, ShortVersion, DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Zips the logs, the settings and version information to the desktop. No maps and nothing
    /// from the game goes in. Returns the path of the zip.
    /// </summary>
    public string ExportFeedback(string? targetDir = null)
    {
        string dir = targetDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string path = Path.Combine(dir, $"LootGoblinsMap-feedback-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var info = new StringBuilder();
        info.AppendLine($"Loot Goblins Map {Version}");
        info.AppendLine($"exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        info.AppendLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, .NET {Environment.Version}");
        info.AppendLine($"maps built: {State.MapsBuilt} on {State.BuiltUtc ?? "-"} in {State.BuildSeconds} s with version {State.BuiltWithVersion ?? "-"}, DLC {(State.DlcBuilt ? "yes" : "no")}");
        info.AppendLine($"archives changed since build: {ArchivesChangedSinceBuild(out _)}");
        var entry = zip.CreateEntry("version.txt");
        using (var w = new StreamWriter(entry.Open()))
            w.Write(info.ToString());

        void Add(string file, string name)
        {
            if (File.Exists(file))
                zip.CreateEntryFromFile(file, name);
        }
        Add(SettingsPath, "settings.json");
        Add(StatePath, "app.json");
        if (Directory.Exists(LogsDir))
            foreach (string log in Directory.EnumerateFiles(LogsDir))
                Add(log, "logs/" + Path.GetFileName(log));
        Add(Path.Combine(MapsDir, "build_report.md"), "build_report.md");
        return path;
    }

    /// <summary>
    /// Opens a folder of the app's own in Explorer, through the Windows shell's automation
    /// object. Returns false when that is not available; the caller then just shows the path.
    /// </summary>
    public static bool OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return false;
            object? shell = Activator.CreateInstance(shellType);
            shellType.InvokeMember("Open", BindingFlags.InvokeMethod, null, shell, new object[] { folder });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---- the game's state, for clear messages ----------------------------------------------

    /// <summary>
    /// The screen mode in the game's own settings file under the user's profile (not in the game
    /// folder): "FULLSCREEN" means exclusive fullscreen, where no overlay can be shown.
    /// </summary>
    public static string? GameScreenMode()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkSoulsIII", "GraphicsConfig.xml");
            if (!File.Exists(path))
                return null;
            string text = File.ReadAllText(path);   // the BOM tells .NET it is UTF-16
            var m = System.Text.RegularExpressions.Regex.Match(text, "<ScreenMode>\\s*([^<]+?)\\s*</ScreenMode>");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
