using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MapCore;

/// <summary>Reads Steam's library list (libraryfolders.vdf) to find where games are installed. Text in, paths out.</summary>
public static class SteamLibrary
{
    public const string AppId = "374320";
    public const string GameFolderName = "DARK SOULS III";

    /// <summary>The library folders named in a libraryfolders.vdf, in file order. Tolerates both the old and the new layout of the file.</summary>
    public static List<string> ParseLibraryFolders(string vdfText)
    {
        var folders = new List<string>();
        // New layout: "path" "D:\\SteamLibrary". Old layout: "1" "D:\\SteamLibrary".
        foreach (Match m in Regex.Matches(vdfText ?? "", "\"(?<key>path|\\d+)\"\\s+\"(?<value>(?:[^\"\\\\]|\\\\.)*)\""))
        {
            string value = Regex.Replace(m.Groups["value"].Value, @"\\(.)", "$1");
            // The old layout's numeric keys also appear with non-path values in the new one ("apps" lists); keep only paths.
            if (value.Length >= 3 && (value[1] == ':' || value.StartsWith("\\\\") || value.StartsWith('/')))
                if (!folders.Contains(value, StringComparer.OrdinalIgnoreCase))
                    folders.Add(value);
        }
        return folders;
    }

    /// <summary>Where the game would be inside a library folder.</summary>
    public static string GamePathIn(string libraryFolder) => Path.Combine(libraryFolder, "steamapps", "common", GameFolderName);

    /// <summary>
    /// The first candidate library that really holds the game (its archives are there), or null.
    /// <paramref name="steamRoots"/> are Steam install folders; each is itself a library and names the others.
    /// </summary>
    public static string FindGame(IEnumerable<string> steamRoots)
    {
        var libraries = new List<string>();
        foreach (string root in steamRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            libraries.Add(root);
            foreach (string vdf in new[] { Path.Combine(root, "config", "libraryfolders.vdf"), Path.Combine(root, "steamapps", "libraryfolders.vdf") })
            {
                try
                {
                    if (File.Exists(vdf))
                        libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string game = GamePathIn(library);
            if (IsGameFolder(game))
                return game;
        }
        return null;
    }

    /// <summary>A folder is the game when its archives are there (in it or in its "Game" subfolder).</summary>
    public static bool IsGameFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(Path.Combine(path, "Data1.bhd")) || File.Exists(Path.Combine(path, "Game", "Data1.bhd"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The folder that holds the archives, given the game folder or its "Game" subfolder.</summary>
    public static string ArchiveFolder(string gamePath) =>
        File.Exists(Path.Combine(gamePath, "Data1.bhd")) ? gamePath : Path.Combine(gamePath, "Game");
}

/// <summary>
/// A fingerprint of the game's archives (names, sizes, modified times): cheap to compute, and
/// different whenever a patch or a DLC download has changed them, which is when the maps
/// should be rebuilt. No archive content is read.
/// </summary>
public static class BuildStamp
{
    public static string Compute(string gamePath)
    {
        string dir = SteamLibrary.ArchiveFolder(gamePath);
        var sb = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".bhd" && ext != ".bdt")
                continue;
            var info = new FileInfo(file);
            sb.Append(info.Name.ToLowerInvariant()).Append('|').Append(info.Length).Append('|')
              .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return Describe(sb.ToString());
    }

    /// <summary>The stamp of a listing (exposed for tests).</summary>
    public static string Describe(string listing) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(listing)))[..32];
}

/// <summary>What the app remembers between runs (app.json in the data folder).</summary>
public sealed class AppState
{
    public string GamePath { get; set; }
    public bool FlipX { get; set; }
    public bool FlipZ { get; set; }
    public int Rotation { get; set; }

    /// <summary>The archive fingerprint the maps were built from, and facts about that build.</summary>
    public string BuiltStamp { get; set; }
    public string BuiltUtc { get; set; }
    public string BuiltWithVersion { get; set; }
    public double BuildSeconds { get; set; }
    public int MapsBuilt { get; set; }
    public bool DlcBuilt { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static AppState Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOpts) ?? new AppState() : new AppState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AppState();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}

/// <summary>The "something is wrong here" note a player drops with a hotkey.</summary>
public static class FeedbackNote
{
    /// <summary>One block of text for feedback.log: when, which version, where, and what is around.</summary>
    public static string Format(TrackingSession session, string version, DateTime time, int nearest = 5)
    {
        var c = CultureInfo.InvariantCulture;
        var f = session.Current;
        var sb = new StringBuilder();
        sb.Append(c, $"{time:yyyy-MM-ddTHH:mm:ss} mark version={version} map={session.Map.Id} area=\"{session.Map.Name}\" state={f.State} layer={f.Layer}");
        if (f.HasPosition)
        {
            string distance = float.IsNaN(f.Distance) ? "-" : f.Distance.ToString("F2", c);
            sb.Append(c, $" world=({f.World.X:F2}, {f.World.Y:F2}, {f.World.Z:F2}) polygon={f.Kind}:{distance}");
        }
        sb.Append(c, $" gameMap={f.GameMapId ?? "-"} chosenBy=\"{session.MapMethod}\"");
        sb.AppendLine();
        if (f.HasPosition)
        {
            var near = session.Map.Markers
                .Where(m => !m.Unused && m.Layer >= 0)
                .Select(m => (Marker: m, Distance: Vector3.Distance(m.WorldPosition, f.World)))
                .OrderBy(x => x.Distance).Take(nearest);
            foreach (var (marker, distance) in near)
            {
                string collected = session.IsFlagKnown(marker) ? session.IsCollected(marker).ToString() : "unknown";
                string offMesh = marker.OffMesh ? " offMesh" : "";
                sb.AppendLine(string.Create(c, $"    {distance,6:F1} m  {marker.Type} \"{marker.Label}\" layer={marker.Layer} flag={marker.Flag} collected={collected}{offMesh}"));
            }
        }
        return sb.ToString();
    }
}
