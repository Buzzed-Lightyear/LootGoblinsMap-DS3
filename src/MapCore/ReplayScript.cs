using System.Globalization;
using System.Numerics;

namespace MapCore;

/// <summary>One tick of a replay: where the player is, which map the "game" reports, and the flags that change on this tick.</summary>
public sealed class ReplayStep
{
    public Vector3 Position;
    /// <summary>Null when the replay does not say (the tracker then has to go by position).</summary>
    public string MapId;
    public Dictionary<int, bool> FlagChanges = new();
}

/// <summary>
/// A replay with map switches and flag changes, for testing without the game. Text format, one
/// entry per line:
///   x,y,z              a position (as in <see cref="ReplayFile"/>; an optional leading time is ignored)
///   @map m30_01_00_00  from here on the game reports this map id ("@map none" stops reporting one)
///   @flag 53000100 on  this flag changes on the next position ("on" or "off")
/// Blank lines and lines starting with '#' are skipped.
/// </summary>
public sealed class ReplayScript
{
    public List<ReplayStep> Steps { get; } = new();

    /// <summary>Flags set before the first position: the state the "save" starts in.</summary>
    public Dictionary<int, bool> InitialFlags { get; } = new();

    public static ReplayScript Read(string path) => Parse(File.ReadLines(path));

    public static ReplayScript Parse(IEnumerable<string> lines)
    {
        var script = new ReplayScript();
        string map = null;
        var pending = new Dictionary<int, bool>();
        int lineNo = 0;
        foreach (string raw in lines)
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '@')
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0].ToLowerInvariant())
                {
                    case "@map" when parts.Length == 2:
                        if (parts[1].Equals("none", StringComparison.OrdinalIgnoreCase))
                            map = null;
                        else if (MapIds.TryParse(parts[1], out _, out _))
                            map = parts[1];
                        else
                            throw new InvalidDataException($"Replay line {lineNo}: '{parts[1]}' is not a map id.");
                        break;
                    case "@flag" when parts.Length == 3:
                        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int flag) || flag <= 0)
                            throw new InvalidDataException($"Replay line {lineNo}: '{parts[1]}' is not a flag id.");
                        bool on = parts[2].ToLowerInvariant() switch
                        {
                            "on" or "true" or "1" => true,
                            "off" or "false" or "0" => false,
                            _ => throw new InvalidDataException($"Replay line {lineNo}: flag state must be 'on' or 'off', not '{parts[2]}'."),
                        };
                        pending[flag] = on;
                        break;
                    default:
                        throw new InvalidDataException($"Replay line {lineNo}: unknown directive '{raw}'.");
                }
                continue;
            }

            var position = ReplayFile.Parse(new[] { line })[0];
            var step = new ReplayStep { Position = position, MapId = map };
            if (script.Steps.Count == 0)
            {
                foreach (var kv in pending)
                    script.InitialFlags[kv.Key] = kv.Value;
            }
            else
            {
                foreach (var kv in pending)
                    step.FlagChanges[kv.Key] = kv.Value;
            }
            pending.Clear();
            script.Steps.Add(step);
        }
        return script;
    }

    public void Write(string path, string headerComment = null)
    {
        var c = CultureInfo.InvariantCulture;
        using var w = new StreamWriter(path);
        if (!string.IsNullOrEmpty(headerComment))
            foreach (string line in headerComment.Split('\n'))
                w.WriteLine("# " + line.TrimEnd());
        foreach (var kv in InitialFlags)
            w.WriteLine(string.Create(c, $"@flag {kv.Key} {(kv.Value ? "on" : "off")}"));
        string map = null;
        bool first = true;
        foreach (var step in Steps)
        {
            if (first || step.MapId != map)
            {
                w.WriteLine("@map " + (step.MapId ?? "none"));
                map = step.MapId;
                first = false;
            }
            foreach (var kv in step.FlagChanges)
                w.WriteLine(string.Create(c, $"@flag {kv.Key} {(kv.Value ? "on" : "off")}"));
            w.WriteLine(string.Create(c, $"{step.Position.X:F3},{step.Position.Y:F3},{step.Position.Z:F3}"));
        }
    }
}
