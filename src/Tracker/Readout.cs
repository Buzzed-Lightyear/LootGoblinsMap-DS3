using System.Globalization;
using MapCore;

namespace Tracker;

/// <summary>Text for the debug readout and the state messages, shared by the window and the PNG export.</summary>
public static class Readout
{
    public const string MapName = "High Wall of Lothric";

    public static List<string> Lines(TrackingSession session)
    {
        var f = session.Current;
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "status   " + Status(f),
            "version  " + (string.IsNullOrEmpty(f.GameVersion) ? "-" : f.GameVersion),
        };

        if (f.HasPosition)
        {
            lines.Add(string.Create(c, $"world    x {f.World.X,9:F2}  y {f.World.Y,8:F2}  z {f.World.Z,9:F2}"));
            lines.Add(string.Create(c, $"pixel    x {f.PixelX,9:F1}  y {f.PixelY,8:F1}"));
        }
        else
        {
            lines.Add("world    -");
            lines.Add("pixel    -");
        }

        lines.Add(f.State == TrackState.Tracking
            ? $"layer    {f.Layer} of 0..{session.Map.LayerCount - 1}"
            : $"layer    - (last {(session.LastLayer >= 0 ? session.LastLayer.ToString(c) : "-")})");

        lines.Add(f.Kind switch
        {
            LocateKind.Inside => string.Create(c, $"polygon  inside, {f.Distance:F2} m above/below it"),
            LocateKind.Nearest => string.Create(c, $"polygon  nearest, {f.Distance:F2} m away"),
            _ => f.HasPosition ? $"polygon  none within {PolygonLocator.DefaultReach:F0} m" : "polygon  -",
        });

        lines.Add("view     " + session.Transform.Orientation);
        return lines;
    }

    public static string Status(TrackFrame f) => f.State switch
    {
        TrackState.GameNotRunning => "not attached: " + (f.Detail ?? "game not running"),
        TrackState.AccessDenied => "not attached: access denied",
        TrackState.Unsupported => "attached, unsupported game build",
        TrackState.NoCharacter => (f.Detail ?? "attached") + ", no character loaded",
        TrackState.OutsideMap => (f.Detail ?? "attached") + ", outside the map",
        _ => f.Detail ?? "attached",
    };

    /// <summary>The message shown over the map for every state that is not plain tracking; null while tracking.</summary>
    public static string? Message(TrackFrame f) => f.State switch
    {
        TrackState.GameNotRunning => "Dark Souls III is not running.\nStart the game and load a character; the tracker attaches on its own.",
        TrackState.AccessDenied => "Access denied reading the game.\n" + (f.Detail ?? "") + "\nThe tracker will keep trying.",
        TrackState.Unsupported => "Attached, but this game build is not supported.\n" + (f.Detail ?? ""),
        TrackState.NoCharacter => "No character loaded.\nLoad a save; tracking starts on its own.",
        TrackState.OutsideMap => $"The player is outside {MapName}.\nTracking resumes when you return.",
        _ => null,
    };
}
