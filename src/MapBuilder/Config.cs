using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapBuilder;

/// <summary>Tool configuration. Loaded from a JSON file; game path can be overridden by CLI argument.</summary>
public sealed class Config
{
    /// <summary>Game install root (the folder containing DarkSoulsIII.exe, or its parent).</summary>
    public string GamePath { get; set; } = "";

    /// <summary>External mod override folder (Mod Engine style), or null/empty if none.</summary>
    public string ModPath { get; set; }

    public string MapId { get; set; } = "m30_00_00_00";

    /// <summary>Output directory for images/JSON (relative paths are resolved against the project root).</summary>
    public string OutDir { get; set; } = "out";

    /// <summary>Working directory for extracted files (never inside the game path).</summary>
    public string WorkDir { get; set; } = "work";

    public double PixelsPerMeter { get; set; } = 6.0;

    /// <summary>Flip the pixel X axis (world X → -pixel X). Single switch per the known orientation limit.</summary>
    public bool FlipX { get; set; }

    /// <summary>Flip the pixel Y axis (world Z → -pixel Y).</summary>
    public bool FlipZ { get; set; }

    /// <summary>Vertical gap (metres) between stacked faces that forces a layer split.</summary>
    public float LayerSplitHeight { get; set; } = 1.8f;

    [JsonIgnore]
    public string ProjectRoot { get; set; } = Directory.GetCurrentDirectory();

    public static Config Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidDataException($"Empty config: {path}");
        cfg.ProjectRoot = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        return cfg;
    }

    public string ResolveWorkDir() => ResolveAgainstRoot(WorkDir);
    public string ResolveOutDir() => ResolveAgainstRoot(OutDir);

    private string ResolveAgainstRoot(string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(ProjectRoot, p));

    /// <summary>The folder actually containing the Data archives ("Game" subfolder on a standard install).</summary>
    public string ResolveGameDataDir()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
            throw new InvalidDataException("GamePath is not set (config file or --game-path).");
        string root = GamePath;
        if (File.Exists(Path.Combine(root, "Data1.bhd")))
            return root;
        string sub = Path.Combine(root, "Game");
        if (File.Exists(Path.Combine(sub, "Data1.bhd")))
            return sub;
        throw new DirectoryNotFoundException($"Data1.bhd not found under '{root}' or '{sub}'.");
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };
}
