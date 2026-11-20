using MapCore;

namespace Tracker.Sources;

/// <summary>Something the tracker can ask for the player's position, 20 to 30 times a second, and for event flags.</summary>
public interface IPositionSource : IDisposable
{
    PositionSample Read();

    /// <summary>The current state of the given event flags, or null when they cannot be read right now.</summary>
    IReadOnlyDictionary<int, bool>? ReadFlags(IReadOnlyList<int> flags);
}

/// <summary>Plays back a replay script: one step per tick, then holds the last one.</summary>
public sealed class ReplaySource : IPositionSource
{
    private readonly ReplayScript _script;
    private readonly Dictionary<int, bool> _flags;
    private int _next;

    public ReplaySource(ReplayScript script)
    {
        _script = script;
        _flags = new Dictionary<int, bool>(script.InitialFlags);
    }

    public int Count => _script.Steps.Count;
    public bool Finished => _next >= _script.Steps.Count;

    public PositionSample Read()
    {
        if (_script.Steps.Count == 0)
            return PositionSample.Of(SourceStatus.NoCharacter, "replay", "replay file is empty");
        int i = Math.Min(_next, _script.Steps.Count - 1);
        var step = _script.Steps[i];
        if (_next < _script.Steps.Count)
        {
            foreach (var (flag, on) in step.FlagChanges)
                _flags[flag] = on;
            _next++;
        }
        return new PositionSample(SourceStatus.Ok, step.Position, "replay", $"replay {i + 1}/{_script.Steps.Count}", step.MapId);
    }

    public IReadOnlyDictionary<int, bool> ReadFlags(IReadOnlyList<int> flags) =>
        flags.ToDictionary(f => f, f => _flags.TryGetValue(f, out bool on) && on);

    public void Dispose() { }
}

/// <summary>
/// Polls a source on its own thread so a slow attach (copying and scanning the game's main
/// module) never blocks the window. The UI reads <see cref="Latest"/> and
/// <see cref="LatestFlags"/> on its own timer. Positions are read 25 times a second, the
/// watched flags about twice a second.
/// </summary>
public sealed class SamplePump : IDisposable
{
    private sealed class Box(PositionSample sample) { public readonly PositionSample Sample = sample; }

    private readonly IPositionSource _source;
    private readonly Thread _thread;
    private volatile bool _stop;
    private Box _latest = new(PositionSample.Of(SourceStatus.GameNotRunning, null, "starting"));
    private volatile int[] _watched = Array.Empty<int>();
    private volatile IReadOnlyDictionary<int, bool>? _flags;

    /// <summary>Milliseconds between position reads: 40 ms is 25 readings a second.</summary>
    public const int IntervalMs = 40;
    /// <summary>Milliseconds between flag polls.</summary>
    public const int FlagIntervalMs = 500;

    public SamplePump(IPositionSource source)
    {
        _source = source;
        _thread = new Thread(Run) { IsBackground = true, Name = "position pump" };
        _thread.Start();
    }

    public PositionSample Latest => Volatile.Read(ref _latest).Sample;

    /// <summary>The most recent flag states, or null before the first successful poll.</summary>
    public IReadOnlyDictionary<int, bool>? LatestFlags => _flags;

    /// <summary>Which flags to poll from now on (the shown map's markers).</summary>
    public void Watch(int[] flags) => _watched = flags;

    private void Run()
    {
        var sinceFlags = System.Diagnostics.Stopwatch.StartNew();
        int[] polled = Array.Empty<int>();
        while (!_stop)
        {
            PositionSample sample;
            try
            {
                sample = _source.Read();
                int[] watched = _watched;
                if (sample.Status == SourceStatus.Ok && watched.Length > 0 &&
                    (sinceFlags.ElapsedMilliseconds >= FlagIntervalMs || !ReferenceEquals(watched, polled)))
                {
                    var flags = _source.ReadFlags(watched);
                    if (flags != null)
                        _flags = flags;
                    polled = watched;
                    sinceFlags.Restart();
                }
            }
            catch (Exception ex)
            {
                sample = PositionSample.Of(SourceStatus.GameNotRunning, null, "read error: " + ex.Message);
            }
            Volatile.Write(ref _latest, new Box(sample));
            Thread.Sleep(IntervalMs);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        _source.Dispose();
    }
}
