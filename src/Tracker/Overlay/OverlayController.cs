using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MapCore;
using Tracker.Memory;
using Tracker.Sources;

namespace Tracker.Overlay;

/// <summary>
/// Runs the overlay: keeps the overlay window exactly over the game's client area, hides it
/// when the game is minimized or not in the foreground, reacts to the global hotkeys and the
/// optional controller chord, and redraws at most 30 times a second.
/// It only ever asks Windows about the game's window; it never sends it anything.
/// </summary>
internal sealed class OverlayController
{
    private const int WmHotKey = 0x0312;
    private const int HotFullMap = 1, HotLayerUp = 2, HotLayerDown = 3, HotSettings = 4, HotToggle = 5;

    /// <summary>Redraw interval: 33 ms caps the overlay at 30 frames a second.</summary>
    public const int FrameIntervalMs = 33;

    private readonly TrackerCore _core;
    private readonly OverlayView _view;
    private readonly OverlayWindow _window;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private SettingsWindow? _settingsWindow;
    private nint _gameWindow;
    private long _nextWindowSearchMs;
    private bool _enabled = true;
    private bool _chordWasHeld;
    private MapData _shownMap;
    private long _noticeUntilMs;
    private readonly List<string> _hotkeyProblems = new();

    public int Frames { get; private set; }
    public IReadOnlyList<string> HotkeyProblems => _hotkeyProblems;
    public OverlayWindow Window => _window;
    public OverlayView View => _view;

    public OverlayController(TrackerCore core)
    {
        _core = core;
        _shownMap = core.Session.Map;
        _view = new OverlayView(core);
        _window = new OverlayWindow(_view);
        _window.SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(_window.Own.Handle)?.AddHook(WindowHook);
            RegisterHotkeys();
        };
        _window.Closed += (_, _) =>
        {
            _timer!.Stop();
            UnregisterHotkeys();
            _settingsWindow?.Close();
            _core.Dispose();
        };

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _window.Show();
        _timer.Start();
    }

    // ---- hotkeys -----------------------------------------------------------------------

    private IEnumerable<(int Id, string Name, string Text)> HotkeyBindings()
    {
        var s = _core.Settings;
        yield return (HotFullMap, "full map", s.FullMapHotkey);
        yield return (HotLayerUp, "layer up", s.LayerUpHotkey);
        yield return (HotLayerDown, "layer down", s.LayerDownHotkey);
        yield return (HotSettings, "settings", s.SettingsHotkey);
        yield return (HotToggle, "show/hide overlay", s.ToggleOverlayHotkey);
    }

    public void RegisterHotkeys()
    {
        UnregisterHotkeys();
        _hotkeyProblems.Clear();
        foreach (var (id, name, text) in HotkeyBindings())
        {
            if (!Hotkey.TryParse(text, out var hotkey, out string error))
            {
                _hotkeyProblems.Add($"{name}: {error}");
                continue;
            }
            if (!NativeMethods.AddHotKey(_window.Own, id, hotkey.Modifiers | Hotkey.ModNoRepeat, (uint)hotkey.VirtualKey))
                _hotkeyProblems.Add($"{name}: '{hotkey}' is already taken by another program");
        }
    }

    private void UnregisterHotkeys()
    {
        for (int id = HotFullMap; id <= HotToggle; id++)
            NativeMethods.RemoveHotKey(_window.Own, id);
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotKey)
            return 0;
        handled = true;
        OnAction((int)wParam);
        return 0;
    }

    private void OnAction(int id)
    {
        var state = _view.State;
        switch (id)
        {
            case HotFullMap:
                state.FullMap = !state.FullMap;
                state.ShownLayer = null;
                break;
            case HotLayerUp:
            case HotLayerDown:
            {
                if (!state.FullMap)
                    break;   // the minimap always shows the layer the player is on
                int layers = _core.Session.Map.LayerCount;
                int player = _core.Session.Current.State == TrackState.Tracking ? _core.Session.Current.Layer : _core.Session.LastLayer;
                int from = state.ShownLayer ?? Math.Max(player, 0);
                state.ShownLayer = Math.Clamp(from + (id == HotLayerUp ? 1 : -1), 0, layers - 1);
                break;
            }
            case HotSettings:
                OpenSettings();
                break;
            case HotToggle:
                _enabled = !_enabled;
                break;
        }
        _view.InvalidateVisual();
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            return;
        }
        _settingsWindow = new SettingsWindow(_core.Settings, _hotkeyProblems, settings =>
        {
            _core.ReplaceSettings(settings);
            RegisterHotkeys();
            Notice(_hotkeyProblems.Count == 0 ? "settings saved" : "settings saved; some hotkeys are unavailable");
            return _hotkeyProblems;
        });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void Notice(string text)
    {
        _view.State.Notice = text;
        _noticeUntilMs = _clock.ElapsedMilliseconds + 4000;
    }

    // ---- per frame ---------------------------------------------------------------------

    private void Tick()
    {
        _core.Tick();
        if (!ReferenceEquals(_core.Session.Map, _shownMap))
        {
            _shownMap = _core.Session.Map;
            _view.State.ShownLayer = null;
            _view.ResetView();
        }
        if (_view.State.Notice != null && _clock.ElapsedMilliseconds > _noticeUntilMs)
            _view.State.Notice = null;

        PollController();

        var (visible, rect) = Placement();
        if (!rect.IsEmpty)
            _window.Cover(rect);
        _window.SetShown(visible && _enabled);
        if (visible && _enabled)
        {
            _view.InvalidateVisual();
            Frames++;
        }
    }

    /// <summary>Where the overlay belongs right now, and whether it should be visible.</summary>
    private (bool Visible, PixelRect Rect) Placement()
    {
        if (_core.IsReplay)
        {
            // No game to sit on: cover the primary screen so the overlay can be tried without the game.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window);
            return (true, new PixelRect(0, 0, (int)(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX), (int)(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY)));
        }

        int? pid = (_core.Source as LiveGameSource)?.ProcessId;
        if (pid == null)
        {
            _gameWindow = 0;
            return (false, default);
        }

        var state = GameWindow.Query(_gameWindow);
        if (!state.Found && _clock.ElapsedMilliseconds >= _nextWindowSearchMs)
        {
            // Look for the window at most once a second.
            _nextWindowSearchMs = _clock.ElapsedMilliseconds + 1000;
            _gameWindow = GameWindow.Find(pid.Value);
            state = GameWindow.Query(_gameWindow);
        }

        nint foreground = NativeMethods.GetForegroundWindow();
        bool trackerInForeground = _settingsWindow != null && foreground == new WindowInteropHelper(_settingsWindow).Handle;
        return OverlayLayout.Place(state, trackerInForeground);
    }

    /// <summary>The optional controller chord toggles the full map on the press, not while held.</summary>
    private void PollController()
    {
        var settings = _core.Settings;
        if (!settings.ControllerChordEnabled || !ControllerChord.TryParse(settings.FullMapControllerChord, out var chord, out _))
        {
            _chordWasHeld = false;
            return;
        }
        bool held = false;
        for (uint pad = 0; pad < 4 && !held; pad++)
            if (NativeMethods.XInputGetState(pad, out var state) == 0)
                held = chord.IsHeld(state.Buttons);
        if (held && !_chordWasHeld)
            OnAction(HotFullMap);
        _chordWasHeld = held;
    }
}
