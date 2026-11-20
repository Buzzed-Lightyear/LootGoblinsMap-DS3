using System.Globalization;
using System.IO;
using MapCore;
using Tracker.App;
using Tracker.Rendering;
using Tracker.Sources;

namespace Tracker;

/// <summary>
/// What every mode of the tracker shares: the maps, the session, the reader thread, the
/// settings and the test log. One <see cref="Tick"/> per frame moves everything forward.
/// </summary>
internal sealed class TrackerCore : IDisposable
{
    private MapData? _watchedMap;
    private IReadOnlyDictionary<int, bool>? _appliedFlags;

    public AppEnvironment Env { get; }
    public TrackingSession Session { get; }
    public SamplePump? Pump { get; }
    public IPositionSource Source { get; }
    public MapRenderer Renderer { get; } = new();
    public TrackerSettings Settings { get; private set; }
    public LiveCheckLog Log { get; }
    public string SourceLabel { get; }
    public bool IsReplay => Source is ReplaySource;

    /// <summary>Goes up each time the settings are replaced, so views know to redraw.</summary>
    public int SettingsVersion { get; private set; }

    /// <param name="startPump">False for the windowless modes, which step the source themselves so a run is repeatable.</param>
    public TrackerCore(AppEnvironment env, IPositionSource source, string sourceLabel, bool startPump = true)
    {
        Env = env;
        Source = source;
        SourceLabel = sourceLabel;
        var config = env.BuildConfig();
        var maps = MapSet.Load(env.MapsDir, config.MapId);
        Settings = TrackerSettings.Load(env.SettingsPath);
        Session = new TrackingSession(maps, config.MapId, config.Orientation);
        Log = new LiveCheckLog(env.LiveLogPath);
        Renderer.Theme = Theme.Load(env.ThemesDir, Settings.Theme);
        Pump = startPump ? new SamplePump(source) : null;
    }

    public static TrackerCore ForApp(AppEnvironment env, IPositionSource source, string sourceLabel) => new(env, source, sourceLabel);

    public void ReplaceSettings(TrackerSettings settings)
    {
        Settings = settings;
        SettingsVersion++;
        Renderer.Theme = Theme.Load(Env.ThemesDir, settings.Theme);
        try
        {
            settings.Save(Env.SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settings.Problems.Add("settings could not be saved: " + ex.Message);
        }
    }

    /// <summary>
    /// One step driven by the caller instead of the reader thread: read the source, apply the
    /// flags of the shown map, write the log. Returns the events of this step.
    /// </summary>
    public List<TrackEvent> StepDirect(DateTime time)
    {
        Session.Update(Source.Read(), time);
        var flags = Source.ReadFlags(Session.WatchedFlags());
        if (flags != null)
            Session.ApplyFlags(flags, time);
        var events = Session.DrainEvents();
        Log.Append(events);
        return events;
    }

    /// <summary>Takes the newest reading, follows a map change, applies flag states and writes the test log.</summary>
    public TrackFrame Tick()
    {
        if (Pump == null)
            throw new InvalidOperationException("This tracker core is stepped directly; it has no reader thread.");
        var frame = Session.Update(Pump.Latest);

        if (!ReferenceEquals(Session.Map, _watchedMap))
        {
            _watchedMap = Session.Map;
            Pump.Watch(Session.WatchedFlags());
        }

        var flags = Pump.LatestFlags;
        if (flags != null && !ReferenceEquals(flags, _appliedFlags))
        {
            _appliedFlags = flags;
            Session.ApplyFlags(flags);
        }

        Log.Append(Session.DrainEvents());
        return frame;
    }

    public void Dispose()
    {
        if (Pump != null)
            Pump.Dispose();
        else
            Source.Dispose();
    }
}

/// <summary>Text for the debug readout and the state messages, shared by the windows and the PNG export.</summary>
internal static class Readout
{
    public static List<string> Lines(TrackingSession session)
    {
        var f = session.Current;
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "status   " + Status(f),
            "version  " + (string.IsNullOrEmpty(f.GameVersion) ? "-" : f.GameVersion),
            $"map      {session.Map.Id}  {session.Map.Name}",
            $"         game says {f.GameMapId ?? "-"}; chosen by {session.MapMethod}",
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

        var (left, total) = session.TreasureCount();
        lines.Add($"items    {left} of {total} left on this map");
        if (f.UnknownMapId != null)
            lines.Add($"WARNING  unsupported game build: map id read as {f.UnknownMapId}");
        lines.Add("view     " + session.Transform.Orientation);
        return lines;
    }

    public static string Status(TrackFrame f) => f.State switch
    {
        TrackState.GameNotRunning => "not attached: " + (f.Detail ?? "game not running"),
        TrackState.AccessDenied => "not attached: access denied",
        TrackState.Unsupported => "attached, unsupported game build",
        TrackState.NoCharacter => (f.Detail ?? "attached") + ", no character loaded",
        TrackState.OutsideMap => (f.Detail ?? "attached") + ", not on the shown map",
        _ => f.Detail ?? "attached",
    };

    /// <summary>The message shown over the map for every state that is not plain tracking; null while tracking.</summary>
    public static string? Message(TrackingSession session, bool brief = false)
    {
        var f = session.Current;
        return f.State switch
        {
            TrackState.GameNotRunning => brief ? "Game not running" : "Dark Souls III is not running.\nStart the game and load a character; the tracker attaches on its own.",
            TrackState.AccessDenied => brief ? "Access denied (game is elevated)" : "Access denied reading the game.\n" + (f.Detail ?? "") + "\nThe tracker will keep trying.",
            TrackState.Unsupported => brief ? "Unsupported game build" : "Attached, but this game build is not supported.\n" + (f.Detail ?? ""),
            TrackState.NoCharacter => brief ? "No character loaded" : "No character loaded.\nLoad a save; tracking starts on its own.",
            TrackState.OutsideMap => f.GameMapId != null && !session.Maps.Has(f.GameMapId)
                ? (brief ? $"No map for {session.Maps.NameOf(f.GameMapId)}" : $"The game is in {session.Maps.NameOf(f.GameMapId)}, which has no built map.\nTracking resumes when you return to a mapped area.")
                : (brief ? "Off the map" : $"The player is not on the map of {session.Map.Name}.\nTracking resumes when you return."),
            _ => null,
        };
    }

    /// <summary>
    /// A standing warning that does not replace the map: the game build is not a known one, so the
    /// map is chosen by position, which cannot tell every pair of maps apart.
    /// </summary>
    public static string? Warning(TrackingSession session) =>
        session.Current.UnknownMapId != null
            ? "Unsupported game version: map chosen by position"
            : null;
}
