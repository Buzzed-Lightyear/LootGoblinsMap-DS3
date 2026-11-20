using System.Numerics;
using MapCore;

namespace Tracker.Sources;

/// <summary>Something the tracker can ask for the player's position, 20 to 30 times a second.</summary>
public interface IPositionSource : IDisposable
{
    PositionSample Read();
}

/// <summary>Plays back a recorded or synthetic list of positions, one per tick, then holds the last one.</summary>
public sealed class ReplaySource : IPositionSource
{
    private readonly IReadOnlyList<Vector3> _positions;
    private int _next;

    public ReplaySource(IReadOnlyList<Vector3> positions)
    {
        _positions = positions;
    }

    public int Count => _positions.Count;
    public bool Finished => _next >= _positions.Count;

    public PositionSample Read()
    {
        if (_positions.Count == 0)
            return PositionSample.Of(SourceStatus.NoCharacter, "replay", "replay file is empty");
        int i = Math.Min(_next, _positions.Count - 1);
        if (_next < _positions.Count)
            _next++;
        return new PositionSample(SourceStatus.Ok, _positions[i], "replay", $"replay {i + 1}/{_positions.Count}");
    }

    public void Dispose() { }
}

/// <summary>
/// Polls a source on its own thread so a slow attach (copying and scanning the game's main
/// module) never blocks the window. The UI reads <see cref="Latest"/> on its own timer.
/// </summary>
public sealed class SamplePump : IDisposable
{
    private sealed class Box(PositionSample sample) { public readonly PositionSample Sample = sample; }

    private readonly IPositionSource _source;
    private readonly Thread _thread;
    private volatile bool _stop;
    private Box _latest = new(PositionSample.Of(SourceStatus.GameNotRunning, null, "starting"));

    /// <summary>Milliseconds between reads: 40 ms is 25 readings a second.</summary>
    public const int IntervalMs = 40;

    public SamplePump(IPositionSource source)
    {
        _source = source;
        _thread = new Thread(Run) { IsBackground = true, Name = "position pump" };
        _thread.Start();
    }

    public PositionSample Latest => Volatile.Read(ref _latest).Sample;

    private void Run()
    {
        while (!_stop)
        {
            PositionSample sample;
            try
            {
                sample = _source.Read();
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
