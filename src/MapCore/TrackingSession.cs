using System.Globalization;
using System.Numerics;

namespace MapCore;

/// <summary>What a position source can report on one tick.</summary>
public enum SourceStatus
{
    /// <summary>The game process was not found.</summary>
    GameNotRunning,
    /// <summary>The game is running but Windows refused read access (the tracker never elevates).</summary>
    AccessDenied,
    /// <summary>Attached, but the player pointers could not be resolved in this game build.</summary>
    Unsupported,
    /// <summary>Attached, but no character is loaded (title screen, menus, loading).</summary>
    NoCharacter,
    /// <summary>A position was read.</summary>
    Ok,
}

/// <summary>One reading from a position source (live game or replay). <paramref name="MapId"/> is the game's own map id when it could be read.</summary>
public readonly record struct PositionSample(SourceStatus Status, Vector3 Position, string GameVersion, string Detail, string MapId = null)
{
    public static PositionSample At(Vector3 position, string version = null) => new(SourceStatus.Ok, position, version, null);
    public static PositionSample AtMap(Vector3 position, string mapId, string version = null) => new(SourceStatus.Ok, position, version, null, mapId);
    public static PositionSample Of(SourceStatus status, string version = null, string detail = null) => new(status, default, version, detail);
}

/// <summary>Where a tick left the tracker; drives both the readout and the message on the map.</summary>
public enum TrackState
{
    GameNotRunning,
    AccessDenied,
    Unsupported,
    NoCharacter,
    /// <summary>A position was read but it is not on or near the shown map's navmesh, or the game is in a map that was not built.</summary>
    OutsideMap,
    /// <summary>The player is on the map; layer and pixel position are valid.</summary>
    Tracking,
}

/// <summary>Everything the UI shows for one tick.</summary>
public sealed class TrackFrame
{
    public TrackState State;
    public string GameVersion;
    public string Detail;
    public bool HasPosition;
    public Vector3 World;
    public double PixelX, PixelY;
    /// <summary>Layer of the polygon under the player, or -1.</summary>
    public int Layer = -1;
    public LocateKind Kind;
    /// <summary>Distance to the located polygon in metres (vertical when inside, 3D otherwise); NaN when none.</summary>
    public float Distance = float.NaN;
    /// <summary>The map id the game reported (null when it could not be read), and the map being shown.</summary>
    public string GameMapId, ShownMapId;
    /// <summary>
    /// Set when the value read as the map id is not a map id of this game at all: the game build
    /// is not one the tracker knows, and the map is being chosen by position instead.
    /// </summary>
    public string UnknownMapId;
}

public enum TrackEventKind { MapSwitch, FlagFlip, BonfireRest }

/// <summary>Something worth a line in the automatic test log.</summary>
public sealed record TrackEvent(DateTime Time, TrackEventKind Kind, string MapId, string Area, string Subject, string Detail);

public enum MarkerVisibility { Hidden, Shown, Dimmed }

/// <summary>
/// The tracker's per-tick logic, free of any UI or process code so it can be unit-tested and
/// replayed: takes a sample, picks the map, finds the polygon and layer, keeps the trail, tracks
/// which markers are collected, and notes the events for the test log.
/// The trail is stored in world space, so changing the orientation re-projects it together
/// with the map, the markers and the dot.
/// </summary>
public sealed class TrackingSession
{
    /// <summary>Beyond this distance from any polygon the player counts as outside the map.</summary>
    public const float OutsideDistance = 12f;
    /// <summary>A jump larger than this between two ticks is a warp, not a walk: the trail breaks.</summary>
    public const float WarpDistance = 25f;
    public const float TrailMinStep = 0.15f;
    public const int TrailCapacity = 4000;
    /// <summary>"At a bonfire": standing still within this distance of a bonfire marker.</summary>
    public const float BonfireRestDistance = 1.5f;
    public static readonly TimeSpan RestTime = TimeSpan.FromSeconds(1);

    public const string MethodMapId = "map id read from memory";
    public const string MethodPosition = "position (no map id available)";
    public const string MethodPositionUnknownId = "position (the map id read is not a known map: unsupported game build)";

    private readonly MapSet _maps;
    private readonly List<Vector3?> _trail = new();
    private readonly Dictionary<int, bool> _flags = new();
    private readonly List<TrackEvent> _events = new();
    private Orientation _orientation;

    // "Came to rest" detection for the bonfire log.
    private Vector3 _restAnchor;
    private DateTime _restSince;
    private bool _restLogged, _hasAnchor;

    public MapData Map { get; private set; }
    public MapSet Maps => _maps;
    public MapTransform Transform { get; private set; }
    public TrackFrame Current { get; private set; } = new() { State = TrackState.GameNotRunning };

    /// <summary>Last layer the player was located on; kept while off-mesh so the highlight does not flicker.</summary>
    public int LastLayer { get; private set; } = -1;

    /// <summary>How the shown map was chosen last: from the game's map id or from the position.</summary>
    public string MapMethod { get; private set; } = "start-up default";

    /// <summary>Recent world positions, oldest first. A null entry is a break (warp or lost tracking).</summary>
    public IReadOnlyList<Vector3?> Trail => _trail;

    /// <summary>Goes up whenever something other than the player's position changes what a map view shows: a flag, the map, the orientation.</summary>
    public int Revision { get; private set; }

    public TrackingSession(MapData map, Orientation orientation) : this(MapSet.Single(map), map.Id, orientation) { }

    public TrackingSession(MapSet maps, string startMapId, Orientation orientation)
    {
        _maps = maps;
        _orientation = orientation;
        Map = maps.Get(startMapId) ?? maps.Ids.Select(maps.Get).FirstOrDefault(m => m != null)
              ?? throw new InvalidDataException("None of the built maps could be loaded.");
        Transform = Map.TransformFor(orientation);
    }

    public void SetOrientation(Orientation orientation)
    {
        _orientation = orientation;
        Transform = Map.TransformFor(orientation);
        Revision++;
        if (Current.HasPosition)
            (Current.PixelX, Current.PixelY) = Transform.ToPixel(Current.World);
    }

    public void ClearTrail() => _trail.Clear();

    /// <summary>Events since the last call, oldest first.</summary>
    public List<TrackEvent> DrainEvents()
    {
        var copy = _events.ToList();
        _events.Clear();
        return copy;
    }

    public TrackFrame Update(PositionSample sample, DateTime? now = null)
    {
        DateTime time = now ?? DateTime.Now;
        var frame = new TrackFrame { GameVersion = sample.GameVersion, Detail = sample.Detail, GameMapId = sample.MapId, ShownMapId = Map.Id };

        if (sample.Status != SourceStatus.Ok)
        {
            frame.State = sample.Status switch
            {
                SourceStatus.AccessDenied => TrackState.AccessDenied,
                SourceStatus.Unsupported => TrackState.Unsupported,
                SourceStatus.NoCharacter => TrackState.NoCharacter,
                _ => TrackState.GameNotRunning,
            };
            BreakTrail();
            _hasAnchor = false;
            return Current = frame;
        }

        var p = sample.Position;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
        {
            frame.State = TrackState.NoCharacter;
            frame.Detail = "position is not a finite number";
            BreakTrail();
            _hasAnchor = false;
            return Current = frame;
        }

        // Which map? The game's own map id decides. Without one, the position does, if exactly one map fits.
        string wanted = null, method = null;
        bool unbuilt = false;
        // A value that is not one of the game's map ids means the fixed offset does not hold the
        // map id in this game build. It is not trusted; the position decides where it can.
        bool unknownId = sample.MapId != null && _maps.Get(sample.MapId) == null && !_maps.Index.IsKnownMapId(sample.MapId);
        if (unknownId)
        {
            frame.UnknownMapId = sample.MapId;
            if (Map.Locator.Locate(p) is not { Found: true, Distance: <= OutsideDistance })
                wanted = _maps.LocateByPosition(p);
            method = MethodPositionUnknownId;
            if (wanted == null)
                MapMethod = method;
        }
        else if (sample.MapId != null)
        {
            if (_maps.Get(sample.MapId) != null)
            {
                wanted = sample.MapId;
                method = MethodMapId;
            }
            else
            {
                unbuilt = true;
            }
        }
        else if (Map.Locator.Locate(p) is not { Found: true, Distance: <= OutsideDistance })
        {
            wanted = _maps.LocateByPosition(p);
            method = MethodPosition;
        }
        if (wanted != null && !string.Equals(wanted, Map.Id, StringComparison.OrdinalIgnoreCase))
            SwitchTo(wanted, method, time);
        else if (wanted != null)
            MapMethod = method;
        frame.ShownMapId = Map.Id;

        frame.HasPosition = true;
        frame.World = p;
        (frame.PixelX, frame.PixelY) = Transform.ToPixel(p);

        if (unbuilt)
        {
            frame.State = TrackState.OutsideMap;
            frame.Detail = $"the game is in {sample.MapId}, which has no built map";
            frame.HasPosition = false;
            BreakTrail();
            _hasAnchor = false;
            return Current = frame;
        }

        var hit = Map.Locator.Locate(p);
        frame.Kind = hit.Kind;
        if (hit.Found)
            frame.Distance = hit.Distance;

        if (!hit.Found || hit.Distance > OutsideDistance)
        {
            frame.State = TrackState.OutsideMap;
            BreakTrail();
            _hasAnchor = false;
            return Current = frame;
        }

        frame.State = TrackState.Tracking;
        frame.Layer = hit.Layer;
        LastLayer = hit.Layer;
        AppendTrail(p);
        NoteRest(p, time);
        return Current = frame;
    }

    private void SwitchTo(string mapId, string method, DateTime time)
    {
        var next = _maps.Get(mapId);
        if (next == null)
            return;
        _events.Add(new TrackEvent(time, TrackEventKind.MapSwitch, mapId, next.Name, Map.Id, method));
        Map = next;
        MapMethod = method;
        Revision++;
        Transform = Map.TransformFor(_orientation);
        LastLayer = -1;
        _trail.Clear();
        _hasAnchor = false;
    }

    // ---- collected state ----------------------------------------------------------------

    /// <summary>The flags worth polling for the shown map: every placed marker's flag.</summary>
    public int[] WatchedFlags() =>
        Map.Markers.Where(m => !m.Unused && m.Flag > 0).Select(m => m.Flag).Distinct().ToArray();

    /// <summary>
    /// Takes freshly read flag states. The first reading of a flag only records it; a later change
    /// is a flip and goes to the event log with the names of what it belongs to.
    /// </summary>
    public void ApplyFlags(IReadOnlyDictionary<int, bool> states, DateTime? now = null)
    {
        DateTime time = now ?? DateTime.Now;
        foreach (var (flag, state) in states)
        {
            bool known = _flags.TryGetValue(flag, out bool previous);
            _flags[flag] = state;
            if (!known || previous != state)
                Revision++;
            if (!known || previous == state)
                continue;
            var owners = Map.Markers.Where(m => !m.Unused && m.Flag == flag).ToList();
            string subject = owners.Count == 0 ? $"flag {flag}" : string.Join(" / ", owners.Select(m => m.Label));
            _events.Add(new TrackEvent(time, TrackEventKind.FlagFlip, Map.Id, Map.Name, subject,
                string.Create(CultureInfo.InvariantCulture, $"flag={flag} state={(state ? "on" : "off")}")));
        }
    }

    /// <summary>True once the marker's flag has been read as set (picked up, or lit for a bonfire).</summary>
    public bool IsCollected(MapDocument.MarkerInfo marker) =>
        marker.Flag > 0 && _flags.TryGetValue(marker.Flag, out bool on) && on;

    /// <summary>Whether a marker's state is known at all yet (its flag has been read at least once).</summary>
    public bool IsFlagKnown(MapDocument.MarkerInfo marker) => marker.Flag > 0 && _flags.ContainsKey(marker.Flag);

    public MarkerVisibility VisibilityOf(MapDocument.MarkerInfo marker, TrackerSettings settings)
    {
        if (marker.Unused || marker.Layer < 0)
            return MarkerVisibility.Hidden;
        if (marker.IsBonfire)
            return settings.ShowBonfires ? MarkerVisibility.Shown : MarkerVisibility.Hidden;
        if (!settings.IsCategoryShown(marker.Category))
            return MarkerVisibility.Hidden;
        if (IsCollected(marker))
            return settings.ShowCollected ? MarkerVisibility.Dimmed : MarkerVisibility.Hidden;
        return MarkerVisibility.Shown;
    }

    /// <summary>Treasures of the shown map that are still to be picked up, and how many there are in all.</summary>
    public (int Left, int Total) TreasureCount()
    {
        var treasures = Map.Markers.Where(m => !m.IsBonfire && !m.Unused && m.Layer >= 0).ToList();
        return (treasures.Count(m => !IsCollected(m)), treasures.Count);
    }

    // ---- trail and rest -----------------------------------------------------------------

    private void AppendTrail(Vector3 p)
    {
        Vector3? last = _trail.Count > 0 ? _trail[^1] : null;
        if (last.HasValue)
        {
            float step = Vector3.Distance(last.Value, p);
            if (step < TrailMinStep)
                return;
            if (step > WarpDistance)
                _trail.Add(null);
        }
        _trail.Add(p);
        if (_trail.Count > TrailCapacity)
            _trail.RemoveRange(0, _trail.Count - TrailCapacity);
    }

    private void BreakTrail()
    {
        if (_trail.Count > 0 && _trail[^1].HasValue)
            _trail.Add(null);
    }

    /// <summary>Logs once each time the player comes to rest next to a bonfire marker.</summary>
    private void NoteRest(Vector3 p, DateTime time)
    {
        if (!_hasAnchor || Vector3.Distance(p, _restAnchor) > 0.05f)
        {
            // Moved: restart the clock; a new rest is only logged after really leaving the spot.
            if (_hasAnchor && _restLogged && Vector3.Distance(p, _restAnchor) < BonfireRestDistance)
                return;
            _restAnchor = p;
            _restSince = time;
            _restLogged = false;
            _hasAnchor = true;
            return;
        }
        if (_restLogged || time - _restSince < RestTime)
            return;

        MapDocument.MarkerInfo nearest = null;
        float best = float.MaxValue;
        foreach (var marker in Map.Markers)
        {
            if (!marker.IsBonfire || marker.Layer < 0)
                continue;
            float d = Vector3.Distance(marker.WorldPosition, p);
            if (d < best)
            {
                best = d;
                nearest = marker;
            }
        }
        _restLogged = true;
        if (nearest == null || best > BonfireRestDistance)
            return;
        _events.Add(new TrackEvent(time, TrackEventKind.BonfireRest, Map.Id, Map.Name, nearest.Label,
            string.Create(CultureInfo.InvariantCulture, $"distance={best:F2}")));
    }
}

/// <summary>Formats tracker events as the lines of <c>work/live_checks.log</c> and appends them.</summary>
public sealed class LiveCheckLog
{
    private readonly string _path;

    public LiveCheckLog(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public static string Format(TrackEvent e)
    {
        string time = e.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return e.Kind switch
        {
            TrackEventKind.BonfireRest => $"{time} bonfire map={e.MapId} area=\"{e.Area}\" bonfire=\"{e.Subject}\" {e.Detail}",
            TrackEventKind.FlagFlip => $"{time} flag map={e.MapId} area=\"{e.Area}\" item=\"{e.Subject}\" {e.Detail}",
            _ => $"{time} map from={e.Subject} to={e.MapId} area=\"{e.Area}\" method=\"{e.Detail}\"",
        };
    }

    /// <summary>Appends the events. Failures to write are swallowed: the log must never stop the tracker.</summary>
    public void Append(IEnumerable<TrackEvent> events)
    {
        var lines = events.Select(Format).ToList();
        if (lines.Count == 0)
            return;
        try
        {
            string dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllLines(_path, lines);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
