using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MapBuilder;
using MapCore;
using Microsoft.Win32;
using Tracker.Memory;
using Tracker.Overlay;
using Tracker.Sources;

namespace Tracker.App;

/// <summary>
/// The app's main window: finds the game, builds the maps on first run (and again when the
/// game's archives have changed), starts and stops the overlay, and holds the feedback tools.
/// An ordinary window. It never starts, stops or touches the game.
/// </summary>
internal sealed class LauncherWindow : Window
{
    private readonly AppEnvironment _env;
    private readonly TextBlock _gameLine = Line(), _mapsLine = Line(), _statusLine = Line(), _warnLine = Line();
    private readonly ProgressBar _progress = new() { Height = 14, Margin = new Thickness(0, 8, 0, 2), Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _progressText = new() { Foreground = Brushes.DimGray, Visibility = Visibility.Collapsed };
    private readonly Button _overlayButton, _rebuildButton, _debugButton, _settingsButton, _changeGameButton;
    private readonly DispatcherTimer _timer;
    private OverlayController? _overlay;
    private TrackerCore? _overlayCore;
    private SettingsWindow? _settingsWindow;
    private bool _building;

    /// <param name="firstRun">False to only show the window (self-check picture): no game search, no build.</param>
    public LauncherWindow(AppEnvironment env, bool firstRun = true)
    {
        _env = env;
        Title = $"Loot Goblins Map {AppEnvironment.ShortVersion}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Loot Goblins Map", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "A read-only loot map overlay for Dark Souls III. It builds its maps from your own game files.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 12),
        });
        panel.Children.Add(_gameLine);
        panel.Children.Add(_mapsLine);
        panel.Children.Add(_statusLine);
        _warnLine.Foreground = Brushes.Firebrick;
        _warnLine.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(_warnLine);
        panel.Children.Add(_progress);
        panel.Children.Add(_progressText);

        _overlayButton = Action("Start overlay", ToggleOverlay, primary: true);
        _debugButton = Action("Debug window", OpenDebugWindow);
        _rebuildButton = Action("Rebuild maps", () => _ = BuildAsync("Rebuilding the maps…"));
        _changeGameButton = Action("Choose game folder…", () => { if (PickGameFolder()) _ = BuildAsync("Building the maps…"); });
        _settingsButton = Action("Settings", OpenSettings);
        var row1 = Row(_overlayButton, _debugButton, _settingsButton);
        var row2 = Row(_rebuildButton, _changeGameButton);
        var row3 = Row(Action("Open data folder", OpenDataFolder), Action("Export feedback", ExportFeedback), Action("Quit", Close));
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        panel.Children.Add(row3);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0),
            Text = "Start the game yourself, in borderless or windowed mode, with Steam not run as administrator. " +
                   "The overlay shows over the game while the game is the active window.",
        });
        Content = panel;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            if (firstRun)
                await FirstRunAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            StopOverlay();
        };
        Refresh();
    }

    // ---- first run and rebuilds --------------------------------------------------------

    private async Task FirstRunAsync()
    {
        if (_env.DevConfigPath == null && !SteamLibrary.IsGameFolder(_env.State.GamePath ?? ""))
        {
            string? found = AppEnvironment.FindGameThroughSteam();
            if (found != null)
            {
                _env.State.GamePath = found;
                _env.SaveState();
            }
            else
            {
                MessageBox.Show(this,
                    "Dark Souls III was not found in any Steam library on this computer.\n\nChoose the game's folder (the one named \"DARK SOULS III\") in the next window.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                if (!PickGameFolder())
                {
                    Refresh();
                    return;
                }
            }
        }
        Refresh();

        if (!_env.MapsExist)
        {
            await BuildAsync("First run: building the maps from your game files…");
        }
        else if (_env.ArchivesChangedSinceBuild(out _))
        {
            var answer = MessageBox.Show(this,
                "The game's files have changed since the maps were built (a patch, or DLC added or removed).\n\nRebuild the maps now? It takes about half a minute.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
                await BuildAsync("Rebuilding the maps…");
        }
    }

    /// <summary>Asks for the game folder until a valid one is chosen or the dialog is cancelled.</summary>
    private bool PickGameFolder()
    {
        while (true)
        {
            var dialog = new OpenFolderDialog { Title = "Choose the DARK SOULS III folder", Multiselect = false };
            if (dialog.ShowDialog(this) != true)
                return false;
            if (SteamLibrary.IsGameFolder(dialog.FolderName))
            {
                _env.State.GamePath = dialog.FolderName;
                _env.SaveState();
                Refresh();
                return true;
            }
            MessageBox.Show(this,
                $"That folder does not contain the game's data files (Data1.bhd was not found in it or in its \"Game\" subfolder):\n\n{dialog.FolderName}",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task BuildAsync(string headline)
    {
        if (_building)
            return;
        if (_env.DevConfigPath == null && !SteamLibrary.IsGameFolder(_env.State.GamePath ?? ""))
        {
            MessageBox.Show(this, "The game folder is not set. Use \"Choose game folder…\".", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StopOverlay();
        _building = true;
        _progress.Visibility = _progressText.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _statusLine.Text = headline;
        Refresh();

        var progress = new Progress<BuildProgress>(p =>
        {
            _progress.Value = p.Fraction;
            _progressText.Text = p.MapId == null ? p.Stage : $"{p.Stage} {p.MapId}  ({p.Done + 1} of {p.Total})";
        });
        var log = new StringWriter();
        try
        {
            var summary = await Task.Run(() => _env.BuildMaps(progress, log));
            string dlc = summary.DlcArchivesPresent ? "" : " No DLC archives were found, so the DLC areas are not included; everything else works without them.";
            string failed = summary.Results.Count(r => r.Built && !r.Passed) is > 0 and var n ? $" {n} maps were built with a failing check (see the build report in the data folder)." : "";
            _statusLine.Text = $"Built {summary.Built} maps in {summary.Elapsed.TotalSeconds:F0} s.{dlc}{failed}";
            if (summary.GameDataProblem != null)
                MessageBox.Show(this, "The maps were built, but item and place names could not be read from this game version:\n\n" + summary.GameDataProblem,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _statusLine.Text = "The maps could not be built.";
            try { File.WriteAllText(Path.Combine(_env.LogsDir, "last_build.log"), log + "\nERROR: " + ex); } catch (IOException) { }
            MessageBox.Show(this, "The maps could not be built:\n\n" + ex.Message + "\n\nThe full log is in the data folder (logs\\last_build.log).",
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _building = false;
            _progress.Visibility = _progressText.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    // ---- overlay ------------------------------------------------------------------------

    private void ToggleOverlay()
    {
        if (_overlay != null)
        {
            StopOverlay();
            Refresh();
            return;
        }
        try
        {
            _overlayCore = TrackerCore.ForApp(_env, new LiveGameSource(), "live");
            _overlay = new OverlayController(_overlayCore, _env);
            _overlay.Window.Closed += (_, _) => { _overlay = null; _overlayCore = null; Refresh(); };
            _overlay.Start();
            if (_overlay.HotkeyProblems.Count > 0)
                MessageBox.Show(this, "Some hotkeys could not be registered:\n\n" + string.Join("\n", _overlay.HotkeyProblems) + "\n\nChange them under Settings.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _overlay = null;
            _overlayCore?.Dispose();
            _overlayCore = null;
            MessageBox.Show(this, "The overlay could not be started:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private void StopOverlay()
    {
        var overlay = _overlay;
        _overlay = null;
        _overlayCore = null;
        overlay?.Window.Close();
    }

    private void OpenDebugWindow()
    {
        try
        {
            var core = TrackerCore.ForApp(_env, new LiveGameSource(), "live");
            new MainWindow(core, _env).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The debug window could not be opened:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        if (_overlay != null)
        {
            _overlay.OpenSettings();
            return;
        }
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        var settings = TrackerSettings.Load(_env.SettingsPath);
        _settingsWindow = new SettingsWindow(settings, Array.Empty<string>(), updated =>
        {
            try { updated.Save(_env.SettingsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new[] { "settings could not be saved: " + ex.Message }; }
            return Array.Empty<string>();
        }, _env);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OpenDataFolder()
    {
        if (!AppEnvironment.OpenFolder(_env.DataDir))
            MessageBox.Show(this, "The data folder is:\n\n" + _env.DataDir, Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportFeedback()
    {
        try
        {
            string zip = _env.ExportFeedback();
            MessageBox.Show(this, "Feedback exported to your desktop:\n\n" + zip + "\n\nIt holds the logs, your settings and version information. No maps and no game files.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The feedback could not be exported:\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- status ---------------------------------------------------------------------------

    /// <summary>Updates the status lines and says plainly what is in the way, if anything.</summary>
    private void Refresh()
    {
        var state = _env.State;
        bool haveGame = _env.DevConfigPath != null || SteamLibrary.IsGameFolder(state.GamePath ?? "");
        _gameLine.Text = _env.DevConfigPath != null ? $"Development setup: {_env.DevConfigPath}"
            : haveGame ? $"Game: {state.GamePath}" : "Game: not found yet";

        if (_env.MapsExist)
        {
            string when = DateTime.TryParse(state.BuiltUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc) ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "earlier";
            _mapsLine.Text = state.MapsBuilt > 0
                ? $"Maps: {state.MapsBuilt} built {when} in {state.BuildSeconds:F0} s{(state.DlcBuilt ? ", DLC areas included" : ", no DLC archives (DLC areas not built)")}"
                : "Maps: built";
        }
        else
        {
            _mapsLine.Text = "Maps: not built yet";
        }

        // What is in the way of reading the game, most serious first.
        string warning = "";
        var game = Process.GetProcessesByName(LiveGameSource.ProcessName);
        var steam = Process.GetProcessesByName("steam");
        try
        {
            if (game.Length > 0 && Elevation.IsProcessElevated(game[0].Id) == true)
                warning = "The game is running as administrator, so it cannot be read. Quit the game and Steam, start Steam normally (not \"Run as administrator\"), then start the game again.";
            else if (game.Length == 0 && steam.Length > 0 && Elevation.IsProcessElevated(steam[0].Id) == true)
                warning = "Steam is running as administrator. A game started from it cannot be read. Quit Steam and start it normally.";
            else if (AppEnvironment.GameScreenMode() is { } mode && mode.Contains("FULL", StringComparison.OrdinalIgnoreCase))
                warning = "The game is set to exclusive fullscreen. No overlay can be shown over that. Switch the game to borderless or windowed mode.";
            else if (_overlayCore != null && _overlayCore.Session.Current.State == TrackState.Unsupported)
                warning = "This game version is not supported: the tracker could not find the player in its memory. " + _overlayCore.Session.Current.Detail;
            else if (_overlayCore != null && _overlayCore.Session.Current.UnknownMapId != null)
                warning = "This game version is not fully supported: the current area cannot be read, so the map is chosen from your position. In a few places that can show the wrong area.";
        }
        finally
        {
            foreach (var p in game.Concat(steam))
                p.Dispose();
        }
        _warnLine.Text = warning;
        _warnLine.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!_building)
        {
            if (_overlay != null)
                _statusLine.Text = "Overlay: running. " + (game.Length > 0 ? Readout.Status(_overlayCore!.Session.Current) : "Waiting for the game to start.");
            else if (!_statusLine.Text.StartsWith("Built ", StringComparison.Ordinal) && !_statusLine.Text.StartsWith("The maps could not", StringComparison.Ordinal))
                _statusLine.Text = _env.MapsExist ? "Overlay: stopped." : "";
        }

        _overlayButton.Content = _overlay != null ? "Stop overlay" : "Start overlay";
        _overlayButton.IsEnabled = !_building && _env.MapsExist;
        _debugButton.IsEnabled = !_building && _env.MapsExist;
        _rebuildButton.IsEnabled = !_building && haveGame;
        _changeGameButton.IsEnabled = !_building && _env.DevConfigPath == null;
        _settingsButton.IsEnabled = !_building;
    }

    private static TextBlock Line() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    private static Button Action(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), MinWidth = 110,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static StackPanel Row(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons)
            row.Children.Add(b);
        return row;
    }
}
