using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MapCore;

namespace Tracker.Overlay;

/// <summary>
/// The settings window: an ordinary window (it does take the focus while open) for the category
/// filters, collected items, the minimap and the hotkeys. Apply saves to the per-user settings
/// file and takes effect at once.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly Func<TrackerSettings, IReadOnlyList<string>> _apply;
    private readonly Dictionary<string, CheckBox> _categories = new();
    private readonly CheckBox _showCollected = Check("Show collected items dimmed (off: hide them)");
    private readonly CheckBox _showBonfires = Check("Show bonfires");
    private readonly ComboBox _corner = new() { Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider _size = Slide(120, 800), _opacity = Slide(0.2, 1.0), _zoom = Slide(1, 15), _labels = Slide(0, 12);
    private readonly TextBox _fullMap = Box(), _layerUp = Box(), _layerDown = Box(), _settings = Box(), _toggle = Box(), _chord = Box();
    private readonly CheckBox _chordEnabled = Check("Controller chord for the full map (the game also receives these buttons)");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public SettingsWindow(TrackerSettings current, IReadOnlyList<string> hotkeyProblems, Func<TrackerSettings, IReadOnlyList<string>> apply)
    {
        _apply = apply;
        Title = "Loot Goblins Map — settings";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ResizeMode = ResizeMode.CanMinimize;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(Header("Items shown"));
        var grid = new WrapPanel();
        foreach (string category in ItemCategories.All)
        {
            var box = Check(ItemCategories.DisplayNames[category]);
            box.Width = 160;
            box.IsChecked = current.IsCategoryShown(category);
            _categories[category] = box;
            grid.Children.Add(box);
        }
        panel.Children.Add(grid);
        _showCollected.IsChecked = current.ShowCollected;
        _showBonfires.IsChecked = current.ShowBonfires;
        panel.Children.Add(_showCollected);
        panel.Children.Add(_showBonfires);

        panel.Children.Add(Header("Minimap"));
        foreach (var corner in Enum.GetValues<Corner>())
            _corner.Items.Add(corner);
        _corner.SelectedItem = current.MinimapCorner;
        panel.Children.Add(Row("Corner", _corner));
        _size.Value = current.MinimapSize;
        _opacity.Value = current.MinimapOpacity;
        _zoom.Value = current.MinimapPixelsPerMeter;
        _labels.Value = current.MinimapLabels;
        _labels.IsSnapToTickEnabled = true;
        _labels.TickFrequency = 1;
        panel.Children.Add(Row("Size", _size));
        panel.Children.Add(Row("Opacity", _opacity));
        panel.Children.Add(Row("Zoom (pixels per metre)", _zoom));
        panel.Children.Add(Row("Name labels", _labels));

        panel.Children.Add(Header("Hotkeys"));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6),
            Text = "Written like F7 or Ctrl+Shift+M. Keys: letters, digits, F1–F24, PageUp, PageDown, Home, End, Insert, Delete, arrows, Num0–Num9. " +
                   "A hotkey is taken away from the game while the tracker runs, so avoid keys you play with.",
        });
        _fullMap.Text = current.FullMapHotkey;
        _layerUp.Text = current.LayerUpHotkey;
        _layerDown.Text = current.LayerDownHotkey;
        _settings.Text = current.SettingsHotkey;
        _toggle.Text = current.ToggleOverlayHotkey;
        panel.Children.Add(Row("Full map", _fullMap));
        panel.Children.Add(Row("Layer up (full map)", _layerUp));
        panel.Children.Add(Row("Layer down (full map)", _layerDown));
        panel.Children.Add(Row("This window", _settings));
        panel.Children.Add(Row("Show / hide overlay", _toggle));

        panel.Children.Add(Header("Controller"));
        _chordEnabled.IsChecked = current.ControllerChordEnabled;
        _chord.Text = current.FullMapControllerChord;
        panel.Children.Add(_chordEnabled);
        panel.Children.Add(Row("Chord", _chord));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Text = "Buttons joined with +: " + string.Join(", ", ControllerChord.ButtonNames),
        });

        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var applyButton = new Button { Content = "Apply and save", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        applyButton.Click += (_, _) => Apply();
        var closeButton = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        closeButton.Click += (_, _) => Close();
        buttons.Children.Add(applyButton);
        buttons.Children.Add(closeButton);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);
        ShowProblems(current.Problems.Concat(hotkeyProblems).ToList(), saved: false);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private void Apply()
    {
        var settings = new TrackerSettings
        {
            ShowCollected = _showCollected.IsChecked == true,
            ShowBonfires = _showBonfires.IsChecked == true,
            MinimapCorner = _corner.SelectedItem is Corner corner ? corner : Corner.TopRight,
            MinimapSize = (int)Math.Round(_size.Value),
            MinimapOpacity = Math.Round(_opacity.Value, 2),
            MinimapPixelsPerMeter = Math.Round(_zoom.Value, 1),
            MinimapLabels = (int)Math.Round(_labels.Value),
            FullMapHotkey = _fullMap.Text.Trim(),
            LayerUpHotkey = _layerUp.Text.Trim(),
            LayerDownHotkey = _layerDown.Text.Trim(),
            SettingsHotkey = _settings.Text.Trim(),
            ToggleOverlayHotkey = _toggle.Text.Trim(),
            ControllerChordEnabled = _chordEnabled.IsChecked == true,
            FullMapControllerChord = _chord.Text.Trim(),
        };
        foreach (var (category, box) in _categories)
            settings.SetCategoryShown(category, box.IsChecked == true);
        settings.Validate();

        // Show what was actually accepted (a rejected shortcut falls back to its default).
        _fullMap.Text = settings.FullMapHotkey;
        _layerUp.Text = settings.LayerUpHotkey;
        _layerDown.Text = settings.LayerDownHotkey;
        _settings.Text = settings.SettingsHotkey;
        _toggle.Text = settings.ToggleOverlayHotkey;
        _chord.Text = settings.FullMapControllerChord;

        var hotkeyProblems = _apply(settings);
        ShowProblems(settings.Problems.Concat(hotkeyProblems).ToList(), saved: true);
    }

    private void ShowProblems(List<string> problems, bool saved)
    {
        if (problems.Count == 0)
        {
            _status.Foreground = Brushes.DarkGreen;
            _status.Text = saved ? "Saved." : "";
            return;
        }
        _status.Foreground = Brushes.Firebrick;
        _status.Text = (saved ? "Saved, with these problems:\n" : "Problems:\n") + string.Join("\n", problems.Select(p => "• " + p));
    }

    private static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 3, 0, 3) };

    private static Slider Slide(double min, double max) => new() { Minimum = min, Maximum = max, Width = 240 };

    private static TextBox Box() => new() { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 6) };

    private static UIElement Row(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var text = new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        if (control is Slider slider)
        {
            var value = new TextBlock { Width = 44, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            void Update() => value.Text = slider.Value.ToString(slider.Maximum <= 1.0 ? "0.00" : slider.Maximum <= 15 ? "0.#" : "0", CultureInfo.InvariantCulture);
            slider.ValueChanged += (_, _) => Update();
            Update();
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
        }
        control.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(control);
        return row;
    }
}
