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

/// <summary>One reading from a position source (live game or replay).</summary>
public readonly record struct PositionSample(SourceStatus Status, Vector3 Position, string GameVersion, string Detail)
{
    public static PositionSample At(Vector3 position, string version = null) => new(SourceStatus.Ok, position, version, null);
    public static PositionSample Of(SourceStatus status, string version = null, string detail = null) => new(status, default, version, detail);
}

/// <summary>Where a tick left the tracker; drives both the readout and the message on the map.</summary>
public enum TrackState
{
    GameNotRunning,
    AccessDenied,
    Unsupported,
    NoCharacter,
    /// <summary>A position was read but it is not on or near this map's navmesh.</summary>
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
}

/// <summary>
/// The tracker's per-tick logic, free of any UI or process code so it can be unit-tested and
/// replayed: takes a sample, finds the polygon and layer, keeps the trail.
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

    private readonly MapData _map;
    private readonly List<Vector3?> _trail = new();

    public MapTransform Transform { get; private set; }
    public TrackFrame Current { get; private set; } = new() { State = TrackState.GameNotRunning };

    /// <summary>Last layer the player was located on; kept while off-mesh so the highlight does not flicker.</summary>
    public int LastLayer { get; private set; } = -1;

    /// <summary>Recent world positions, oldest first. A null entry is a break (warp or lost tracking).</summary>
    public IReadOnlyList<Vector3?> Trail => _trail;

    public MapData Map => _map;

    public TrackingSession(MapData map, Orientation orientation)
    {
        _map = map;
        Transform = map.TransformFor(orientation);
    }

    public void SetOrientation(Orientation orientation)
    {
        Transform = _map.TransformFor(orientation);
        if (Current.HasPosition)
            (Current.PixelX, Current.PixelY) = Transform.ToPixel(Current.World);
    }

    public void ClearTrail() => _trail.Clear();

    public TrackFrame Update(PositionSample sample)
    {
        var frame = new TrackFrame { GameVersion = sample.GameVersion, Detail = sample.Detail };

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
            return Current = frame;
        }

        var p = sample.Position;
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
        {
            frame.State = TrackState.NoCharacter;
            frame.Detail = "position is not a finite number";
            BreakTrail();
            return Current = frame;
        }

        frame.HasPosition = true;
        frame.World = p;
        (frame.PixelX, frame.PixelY) = Transform.ToPixel(p);

        var hit = _map.Locator.Locate(p);
        frame.Kind = hit.Kind;
        if (hit.Found)
            frame.Distance = hit.Distance;

        if (!hit.Found || hit.Distance > OutsideDistance)
        {
            frame.State = TrackState.OutsideMap;
            BreakTrail();
            return Current = frame;
        }

        frame.State = TrackState.Tracking;
        frame.Layer = hit.Layer;
        LastLayer = hit.Layer;
        AppendTrail(p);
        return Current = frame;
    }

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
}
