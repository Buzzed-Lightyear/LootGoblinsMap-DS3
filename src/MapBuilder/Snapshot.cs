using System.Globalization;

namespace MapBuilder;

/// <summary>
/// Writes a sorted listing (relative path, size, mtime as unix seconds) of a directory tree.
/// Used to prove the game folder is byte-identical before and after a run. Cross-platform.
/// </summary>
public static class Snapshot
{
    public static void Write(string root, string outFile)
    {
        var lines = new List<string>();
        var rootFull = Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(file);
            string rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
            double mtime = fi.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
            lines.Add($"{rel}\t{fi.Length}\t{mtime.ToString("F4", CultureInfo.InvariantCulture)}");
        }
        lines.Sort(StringComparer.Ordinal);
        File.WriteAllLines(outFile, lines);
    }
}
