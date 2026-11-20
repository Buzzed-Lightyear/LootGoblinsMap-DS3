using MapBuilder.Archive;
using MapBuilder.Mods;
using MapCore;
using SoulsFormats;

namespace MapBuilder.Pipeline;

public sealed record ExtractedFile(string InternalPath, string LocalPath, string Source);

/// <summary>
/// Locates a map's files (mod override first, archives second), decompresses the DCX wrappers,
/// and stores the decompressed payloads under work/extract/&lt;map&gt;. Only the three files a
/// map needs are touched; the game folder is only ever opened for reading.
/// </summary>
public static class Extractor
{
    public static string MsbPath(string mapId) => $"/map/mapstudio/{mapId}.msb.dcx";
    public static string NvaPath(string mapId) => $"/map/{mapId}/{mapId}.nva.dcx";
    public static string NavmeshPath(string mapId) => $"/map/{mapId}/{mapId}.nvmhktbnd.dcx";

    public static IReadOnlyList<string> NeededPaths(string mapId) => new[]
    {
        MsbPath(mapId),
        NvaPath(mapId),
        NavmeshPath(mapId),
    };

    public static string ExtractDir(Config cfg, string mapId) => Path.Combine(cfg.ResolveWorkDir(), "extract", mapId);

    /// <summary>Extracts one map. <paramref name="mods"/> may be null (no override scan).</summary>
    public static List<ExtractedFile> Extract(ArchiveSet archives, Config cfg, string mapId, ModScanResult mods, TextWriter log)
    {
        string extractDir = ExtractDir(cfg, mapId);
        Directory.CreateDirectory(extractDir);

        var results = new List<ExtractedFile>();
        foreach (var internalPath in NeededPaths(mapId))
        {
            byte[] raw;
            string source;
            if (mods != null && mods.NeededFileOverrides.TryGetValue(internalPath, out var overridePath))
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
            log.WriteLine($"  {internalPath}  <-  {source}  ({raw.Length:N0} B compressed, {payload.Length:N0} B decompressed)");
            results.Add(new ExtractedFile(internalPath, localPath, source));
        }

        return results;
    }
}
