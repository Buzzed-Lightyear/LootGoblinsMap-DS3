using System.Text.Json.Serialization;

namespace MapBuilder.Mods;

public sealed class ModScanResult
{
    public List<string> InjectionDlls { get; set; } = new();
    public List<string> ModConfigFiles { get; set; } = new();
    public List<string> ExtraExecutables { get; set; } = new();
    public string ModEngineIni { get; set; }
    public string ModEngineOverrideDir { get; set; }
    public List<string> LooseGameDataDirs { get; set; } = new();
    public Dictionary<string, string> NeededFileOverrides { get; set; } = new();

    [JsonIgnore]
    public bool AnyNeededFileOverridden => NeededFileOverrides.Count > 0;
}

/// <summary>
/// Detects how mods are installed (read-only inspection) and whether any file this tool
/// needs would be overridden. Resolution order mirrors the game with Mod Engine:
/// mod override dir first, loose files in the game dir second, archives last.
/// </summary>
public static class ModScanner
{
    // Vanilla DS3 ships exactly these loose entries next to the archives.
    private static readonly HashSet<string> VanillaDirs = new(StringComparer.OrdinalIgnoreCase)
        { "movie", "sound" };

    private static readonly HashSet<string> VanillaFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "DarkSoulsIII.exe", "binkw64.dll", "fmod_event64.dll", "fmodex64.dll", "steam_api64.dll",
        "Data0.bdt", "Data0.bhd", "Data1.bdt", "Data1.bhd", "Data2.bdt", "Data2.bhd",
        "Data3.bdt", "Data3.bhd", "Data4.bdt", "Data4.bhd", "Data5.bdt", "Data5.bhd",
        "DLC1.bdt", "DLC1.bhd", "DLC2.bdt", "DLC2.bhd",
    };

    // Directory names the game would load loose game data from (UXM-style unpacked installs).
    private static readonly string[] GameDataDirNames =
        { "map", "chr", "obj", "parts", "param", "event", "script", "msg", "mtd", "sfx", "shader", "menu", "facegen", "font", "other", "action", "remo" };

    public static ModScanResult Scan(string gameDataDir, string externalModDir, IEnumerable<string> neededInternalPaths)
    {
        var result = new ModScanResult();

        foreach (var file in Directory.EnumerateFiles(gameDataDir))
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (VanillaFiles.Contains(name))
                continue;
            if (ext == ".dll")
                result.InjectionDlls.Add(name);
            else if (ext == ".ini" || ext == ".toml")
                result.ModConfigFiles.Add(name);
            else if (ext == ".exe")
                result.ExtraExecutables.Add(name);

            if (name.Equals("modengine.ini", StringComparison.OrdinalIgnoreCase))
            {
                result.ModEngineIni = file;
                result.ModEngineOverrideDir = ParseModEngineOverrideDir(file, gameDataDir);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(gameDataDir))
        {
            string name = Path.GetFileName(dir);
            if (GameDataDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.LooseGameDataDirs.Add(name);
        }

        // Which of the files we need would resolve to a mod copy instead of the archives?
        foreach (var internalPath in neededInternalPaths)
        {
            string rel = internalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            foreach (var (root, label) in OverrideRoots(gameDataDir, externalModDir, result.ModEngineOverrideDir))
            {
                string candidate = Path.Combine(root, rel);
                if (File.Exists(candidate))
                {
                    result.NeededFileOverrides[internalPath] = $"{label}: {candidate}";
                    break;
                }
            }
        }

        return result;
    }

    private static IEnumerable<(string Root, string Label)> OverrideRoots(string gameDataDir, string externalModDir, string modEngineDir)
    {
        if (!string.IsNullOrEmpty(externalModDir) && Directory.Exists(externalModDir))
            yield return (externalModDir, "external mod folder");
        if (!string.IsNullOrEmpty(modEngineDir) && Directory.Exists(modEngineDir))
            yield return (modEngineDir, "Mod Engine override");
        yield return (gameDataDir, "loose file in game folder");
    }

    private static string ParseModEngineOverrideDir(string iniPath, string gameDataDir)
    {
        foreach (var raw in File.ReadLines(iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("modOverrideDirectory", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf('=');
                if (idx < 0) continue;
                var val = line[(idx + 1)..].Trim().Trim('"').TrimStart('\\', '/');
                return Path.Combine(gameDataDir, val);
            }
        }
        return null;
    }
}
