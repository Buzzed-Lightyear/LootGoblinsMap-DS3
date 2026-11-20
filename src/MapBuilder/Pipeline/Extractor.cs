using MapBuilder.Archive;
using MapBuilder.Mods;
using SoulsFormats;

namespace MapBuilder.Pipeline;

public sealed record ExtractedFile(string InternalPath, string LocalPath, string Source);

/// <summary>
/// Locates the map files (mod override first, archives second), decompresses the DCX wrappers,
/// and stores the decompressed payloads under work/extract. Only the files needed for the one
/// target map are touched; the game folder is only ever opened for reading.
/// </summary>
public static class Extractor
{
    public static IReadOnlyList<string> NeededPaths(string mapId) => new[]
    {
        $"/map/mapstudio/{mapId}.msb.dcx",
        $"/map/{mapId}/{mapId}.nva.dcx",
        $"/map/{mapId}/{mapId}.nvmhktbnd.dcx",
    };

    public static List<ExtractedFile> Extract(Config cfg, ModScanResult mods, TextWriter log)
    {
        string gameDataDir = cfg.ResolveGameDataDir();
        string extractDir = Path.Combine(cfg.ResolveWorkDir(), "extract", cfg.MapId);
        Directory.CreateDirectory(extractDir);

        var results = new List<ExtractedFile>();
        using var archives = ArchiveSet.OpenDarkSouls3(gameDataDir);
        log.WriteLine($"Opened archives: {string.Join(", ", archives.ArchiveNames)}");

        foreach (var internalPath in NeededPaths(cfg.MapId))
        {
            byte[] raw;
            string source;
            if (mods.NeededFileOverrides.TryGetValue(internalPath, out var overridePath))
            {
                string fsPath = overridePath[(overridePath.IndexOf(": ") + 2)..];
                raw = File.ReadAllBytes(fsPath);
                source = overridePath;
            }
            else if (archives.TryGetFile(internalPath, out raw, out var archiveName))
            {
                source = $"archive {archiveName}";
            }
            else
            {
                throw new FileNotFoundException($"'{internalPath}' not found in any archive or mod override.");
            }

            byte[] payload = DCX.Is(raw) ? DCX.Decompress(raw) : raw;
            string localName = Path.GetFileNameWithoutExtension(internalPath); // strip ".dcx"
            string localPath = Path.Combine(extractDir, localName);
            File.WriteAllBytes(localPath, payload);
            log.WriteLine($"{internalPath}  <-  {source}  ({raw.Length:N0} B compressed, {payload.Length:N0} B decompressed)");
            results.Add(new ExtractedFile(internalPath, localPath, source));
        }

        return results;
    }
}
