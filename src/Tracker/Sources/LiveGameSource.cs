using System.Diagnostics;
using MapCore;
using SoulMemory.DarkSouls3;
using Tracker.Memory;

namespace Tracker.Sources;

/// <summary>
/// Reads the player position, the current map id and event flags from the running game.
/// Read-only: the process is opened through <see cref="ProcessMemory"/> (read + limited query
/// rights) and nothing in here can change it. Never starts, stops, focuses or sends input to
/// the game. Attaches when the game appears, lets go when it exits, and tries again on its own.
/// </summary>
public sealed class LiveGameSource : IPositionSource
{
    public const string ProcessName = "DarkSoulsIII";

    private static readonly TimeSpan AttachRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailedAttachRetry = TimeSpan.FromSeconds(5);

    private readonly DarkSouls3 _ds3 = new();
    private ProcessMemory? _memory;
    private string? _version;
    private DateTime _nextAttachUtc = DateTime.MinValue;
    private PositionSample _idle = PositionSample.Of(SourceStatus.GameNotRunning, null, "Dark Souls III is not running");

    /// <summary>Human-readable notes from the last attach (addresses, scan results) for diagnostics.</summary>
    public string AttachLog { get; private set; } = "";

    /// <summary>The attached game's process id, for finding its window; null while not attached.</summary>
    public int? ProcessId => _memory?.ProcessId;

    public bool CanReadEventFlags => _memory != null && _ds3.CanReadEventFlags;

    public PositionSample Read()
    {
        if (_memory == null)
        {
            if (DateTime.UtcNow < _nextAttachUtc)
                return _idle;
            _idle = TryAttach();
            if (_memory == null)
                return _idle;
        }

        if (_memory.HasExited)
        {
            Detach();
            _nextAttachUtc = DateTime.UtcNow + AttachRetry;
            return _idle = PositionSample.Of(SourceStatus.GameNotRunning, null, "Dark Souls III has exited");
        }

        string attached = $"attached (PID {_memory.ProcessId})";
        if (!_ds3.IsPlayerLoaded())
            return PositionSample.Of(SourceStatus.NoCharacter, _version, attached);

        if (!_ds3.TryGetPosition(out var position))
        {
            if (_memory.LastReadError == NativeMethods.ErrorAccessDenied)
                return PositionSample.Of(SourceStatus.AccessDenied, _version, "access denied while reading the position");
            // The pointer chain is briefly broken while a character or area loads.
            return PositionSample.Of(SourceStatus.NoCharacter, _version, attached);
        }

        // The game's own idea of which map the player is in. A zero or absurd value means "not known".
        string? mapId = null;
        if (_ds3.TryGetMapId(out uint packed) && packed >> 24 is > 0 and < 100)
            mapId = MapIds.FromPacked(packed);

        return new PositionSample(SourceStatus.Ok, position, _version, attached, mapId);
    }

    public IReadOnlyDictionary<int, bool>? ReadFlags(IReadOnlyList<int> flags)
    {
        if (_memory == null || !_ds3.CanReadEventFlags || !_ds3.IsPlayerLoaded())
            return null;
        var states = new Dictionary<int, bool>(flags.Count);
        foreach (int flag in flags)
            states[flag] = _ds3.ReadEventFlag((uint)flag);
        return states;
    }

    /// <summary>One flag, for the live checks.</summary>
    public bool? ReadFlag(int flag) => ReadFlags(new[] { flag })?[flag];

    private PositionSample TryAttach()
    {
        int pid;
        try
        {
            // Listing processes by name uses the system process table; it opens no handle to the game.
            var processes = Process.GetProcessesByName(ProcessName);
            if (processes.Length == 0)
            {
                _nextAttachUtc = DateTime.UtcNow + AttachRetry;
                return PositionSample.Of(SourceStatus.GameNotRunning, null, "Dark Souls III is not running");
            }
            pid = processes[0].Id;
            foreach (var p in processes)
                p.Dispose();
        }
        catch (Exception ex)
        {
            _nextAttachUtc = DateTime.UtcNow + FailedAttachRetry;
            return PositionSample.Of(SourceStatus.GameNotRunning, null, "could not list processes: " + ex.Message);
        }

        ProcessMemory? memory = null;
        try
        {
            memory = ProcessMemory.Open(pid);
            _version = ReadVersion(memory.ExePath);

            byte[] image = memory.ReadMainModule();
            var failed = _ds3.InitPointers(memory, memory.MainModuleBase, image);

            AttachLog =
                $"PID {pid}, access mask 0x{NativeMethods.ReadOnlyAccess:X} (read + limited query)\n" +
                $"exe {memory.ExePath}\n" +
                $"version {_version}\n" +
                $"main module 0x{memory.MainModuleBase:X}, {memory.MainModuleSize:N0} bytes\n" +
                $"scans failed: {(failed.Count == 0 ? "none" : string.Join(", ", failed))}\n" +
                $"event flags readable: {_ds3.CanReadEventFlags}\n" +
                $"WorldChrMan static 0x{_ds3.WorldChrManStaticAddress:X} (module+0x{_ds3.WorldChrManStaticAddress - memory.MainModuleBase:X})";

            if (!_ds3.CanReadPlayer)
            {
                // Without the player scan there is nothing to track. This also happens for a moment
                // while the game is still starting, so keep retrying.
                memory.Dispose();
                _ds3.ResetPointers();
                _nextAttachUtc = DateTime.UtcNow + FailedAttachRetry;
                return PositionSample.Of(SourceStatus.Unsupported, _version,
                    $"player pointer not found in this game build (failed scans: {string.Join(", ", failed)})");
            }

            _memory = memory;
            return PositionSample.Of(SourceStatus.NoCharacter, _version, $"attached (PID {pid})");
        }
        catch (GameAccessDeniedException ex)
        {
            memory?.Dispose();
            _ds3.ResetPointers();
            _nextAttachUtc = DateTime.UtcNow + FailedAttachRetry;
            bool? gameElevated = Elevation.IsProcessElevated(pid);
            string why = gameElevated == true
                ? "the game is running as administrator (usually because Steam is). Restart Steam and the game without administrator rights; the tracker never runs elevated."
                : ex.Message;
            AttachLog = $"PID {pid}: access denied. Game elevated: {gameElevated?.ToString() ?? "unknown"}.";
            return PositionSample.Of(SourceStatus.AccessDenied, null, why);
        }
        catch (Exception ex)
        {
            // Typically the process went away between listing and opening, or is still starting.
            memory?.Dispose();
            _ds3.ResetPointers();
            _nextAttachUtc = DateTime.UtcNow + FailedAttachRetry;
            AttachLog = $"PID {pid}: attach failed: {ex.Message}";
            return PositionSample.Of(SourceStatus.GameNotRunning, null, "attach failed: " + ex.Message);
        }
    }

    /// <summary>Product version from the executable's version resource (a read of the file on disk).</summary>
    private static string ReadVersion(string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath))
                return "unknown";
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string raw = info.ProductVersion ?? info.FileVersion ?? "unknown";
            return Version.TryParse(raw, out var v)
                ? $"{raw} ({DarkSouls3.GetVersion(v)})"
                : raw;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private void Detach()
    {
        _memory?.Dispose();
        _memory = null;
        _ds3.ResetPointers();
    }

    public void Dispose() => Detach();
}
