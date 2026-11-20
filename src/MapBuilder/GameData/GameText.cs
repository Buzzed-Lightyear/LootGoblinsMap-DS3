using MapBuilder.Archive;
using SoulsFormats;

namespace MapBuilder.GameData;

/// <summary>What a piece of game text names. Each kind is spread over a base file and its DLC additions.</summary>
public enum TextKind
{
    Goods,
    Weapon,
    Protector,
    Accessory,
    Magic,
    Npc,
    Place,
    Menu,
}

/// <summary>
/// The game's own text, read at build time from the message archives of one language
/// (<c>/msg/&lt;language&gt;/item*.msgbnd.dcx</c> and <c>menu*.msgbnd.dcx</c>). Nothing from here is
/// stored in the repository; the builder writes what it resolves into <c>out/</c>.
/// </summary>
public sealed class GameText
{
    /// <summary>
    /// Binder file ids inside the message archives, per kind: the base file first, then the
    /// DLC1 and DLC2 additions. Verified against the 1.15.2 archives.
    /// </summary>
    private static readonly Dictionary<TextKind, int[]> FileIds = new()
    {
        [TextKind.Goods] = new[] { 10, 210, 250 },
        [TextKind.Weapon] = new[] { 11, 211, 251 },
        [TextKind.Protector] = new[] { 12, 212, 252 },
        [TextKind.Accessory] = new[] { 13, 213, 253 },
        [TextKind.Magic] = new[] { 14, 214, 254 },
        [TextKind.Npc] = new[] { 18, 215, 255 },
        [TextKind.Place] = new[] { 19, 216, 256 },
        [TextKind.Menu] = new[] { 200, 232, 272 },
    };

    private static readonly Dictionary<int, TextKind> KindByFileId =
        FileIds.SelectMany(kv => kv.Value.Select(id => (id, kv.Key))).ToDictionary(x => x.id, x => x.Key);

    private readonly Dictionary<TextKind, Dictionary<int, string>> _text =
        Enum.GetValues<TextKind>().ToDictionary(k => k, _ => new Dictionary<int, string>());

    public string Language { get; private init; } = "";

    /// <summary>The archives each text binder was found in, for the build log.</summary>
    public List<string> Sources { get; } = new();

    public int Count(TextKind kind) => _text[kind].Count;

    /// <summary>The text for an id, or null when there is none (missing or blank).</summary>
    public string Get(TextKind kind, int id) => _text[kind].TryGetValue(id, out var s) ? s : null;

    /// <summary>Adds one FMG file as it sits in a message binder. Files of no interest are ignored.</summary>
    public void AddFmg(int binderFileId, byte[] fmgBytes)
    {
        if (!KindByFileId.TryGetValue(binderFileId, out var kind))
            return;
        var fmg = FMG.Read(fmgBytes);
        foreach (var entry in fmg.Entries)
        {
            // Later files (DLC additions) override earlier ones; blank entries never hide a real one.
            if (!string.IsNullOrWhiteSpace(entry.Text))
                _text[kind][entry.ID] = entry.Text.Trim();
        }
    }

    /// <summary>
    /// Loads the item and menu text of one language. The archives ship the newest binder under a
    /// DLC-suffixed name that contains everything before it, so the newest present one is used.
    /// </summary>
    public static GameText Load(ArchiveSet archives, string language, TextWriter log)
    {
        var text = new GameText { Language = language };
        foreach (string family in new[] { "item", "menu" })
        {
            bool found = false;
            foreach (string suffix in new[] { "_dlc2", "_dlc1", "" })
            {
                string path = $"/msg/{language}/{family}{suffix}.msgbnd.dcx";
                if (!archives.TryGetFile(path, out var raw, out var source))
                    continue;
                var bnd = BND4.Read(DCX.Is(raw) ? DCX.Decompress(raw) : raw);
                // Base files first, then DLC additions, whatever order the binder lists them in.
                foreach (var file in bnd.Files.OrderBy(f => f.ID))
                    text.AddFmg(file.ID, file.Bytes);
                text.Sources.Add($"{path} ({source})");
                found = true;
                break;
            }
            if (!found)
                throw new FileNotFoundException($"No {family} message archive found for language '{language}' (looked for /msg/{language}/{family}[_dlc2|_dlc1].msgbnd.dcx).");
        }
        log.WriteLine($"Text ({language}): " + string.Join(", ", Enum.GetValues<TextKind>().Select(k => $"{k} {text.Count(k)}")));
        return text;
    }
}
