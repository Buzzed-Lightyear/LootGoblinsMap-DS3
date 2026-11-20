using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MapCore;
using Tracker.Rendering;
using Tracker.Sources;
using Orientation = MapCore.Orientation;

namespace Tracker;

/// <summary>
/// The tracker window: the map on the left, the debug readout and the controls on the right.
/// An ordinary resizable window; it never touches the game's window.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TrackingSession _session;
    private readonly SamplePump _pump;
    private readonly MapView _mapView;
    private readonly string _configPath;
    private readonly DispatcherTimer _timer;

    private readonly TextBlock _readout = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _follow = Check("Follow player", true);
    private readonly CheckBox _fit = Check("Fit map to window", false);
    private readonly CheckBox _topmost = Check("Always on top", false);
    private readonly CheckBox _flipX = Check("Flip X", false);
    private readonly CheckBox _flipZ = Check("Flip Z", false);
    private readonly TextBlock _rotationLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Slider _zoom = new() { Minimum = Math.Log2(ViewState.MinZoom), Maximum = Math.Log2(ViewState.MaxZoom), Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _saveStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };
    private int _rotation;
    private bool _syncing;

    public MainWindow(MapData map, Orientation orientation, IPositionSource source, string configPath, string sourceLabel)
    {
        _configPath = configPath;
        _session = new TrackingSession(map, orientation);
        _pump = new SamplePump(source);
        _mapView = new MapView(new MapRenderer(map), _session);
        _mapView.ViewChangedByUser += SyncControlsFromView;

        Title = $"Loot Goblins Map — {Readout.MapName} ({sourceLabel})";
        Width = 1240;
        Height = 780;
        MinWidth = 640;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x11, 0x17));

        _flipX.IsChecked = orientation.FlipX;
        _flipZ.IsChecked = orientation.FlipZ;
        _rotation = orientation.QuarterTurns;

        Content = BuildLayout();
        SyncControlsFromView();
        UpdateRotationLabel();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(SamplePump.IntervalMs) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _pump.Dispose();
        };
    }

    private UIElement BuildLayout()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };

        panel.Children.Add(Header("Readout"));
        panel.Children.Add(_readout);

        panel.Children.Add(Header("View"));
        panel.Children.Add(_follow);
        panel.Children.Add(_fit);
        panel.Children.Add(new TextBlock { Text = "Zoom (mouse wheel works too)", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(_zoom);
        panel.Children.Add(_topmost);
        var clear = new Button { Content = "Clear trail", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => _session.ClearTrail();
        panel.Children.Add(clear);

        panel.Children.Add(Header("Orientation"));
        panel.Children.Add(_flipX);
        panel.Children.Add(_flipZ);
        var rotate = new Button { Content = "Rotate 90°", Padding = new Thickness(8, 3, 8, 3) };
        rotate.Click += (_, _) =>
        {
            _rotation = (_rotation + 1) % 4;
            UpdateRotationLabel();
            ApplyOrientation();
        };
        var rotateRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        rotateRow.Children.Add(rotate);
        rotateRow.Children.Add(_rotationLabel);
        panel.Children.Add(rotateRow);
        var save = new Button { Content = "Save orientation to config.json", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) => SaveOrientation();
        panel.Children.Add(save);
        panel.Children.Add(_saveStatus);

        panel.Children.Add(Header("Legend"));
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Bright floor: the layer you are on. Dim floor: the other layers.\n" +
                   "Orange circle: bonfire. Yellow diamond: treasure.\n" +
                   "Red dot: you. Blue line: where you have been.\n" +
                   "Drag the map to look around (turns follow off).",
        });

        _follow.Click += (_, _) => { _mapView.View.Follow = _follow.IsChecked == true; _mapView.InvalidateVisual(); };
        _fit.Click += (_, _) => { _mapView.View.FitToWindow = _fit.IsChecked == true; _mapView.InvalidateVisual(); };
        _topmost.Click += (_, _) => Topmost = _topmost.IsChecked == true;
        _flipX.Click += (_, _) => ApplyOrientation();
        _flipZ.Click += (_, _) => ApplyOrientation();
        _zoom.ValueChanged += (_, _) =>
        {
            if (_syncing)
                return;
            _mapView.View.Zoom = Math.Pow(2, _zoom.Value);
            if (_mapView.View.FitToWindow)
            {
                _mapView.View.FitToWindow = false;
                _fit.IsChecked = false;
            }
            _mapView.InvalidateVisual();
        };

        var side = new Border
        {
            Width = 356,
            Background = new SolidColorBrush(Color.FromRgb(0xf4, 0xf5, 0xf7)),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel },
        };
        DockPanel.SetDock(side, Dock.Right);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(side);
        root.Children.Add(_mapView);
        return root;
    }

    /// <summary>
    /// Self-check: after the given number of ticks, save a picture of this window's content and close.
    /// The window is shown without taking the keyboard focus.
    /// </summary>
    public void SaveSnapshotAndClose(string pngPath, int afterTicks)
    {
        ShowActivated = false;
        int ticks = 0;
        _timer.Tick += (_, _) =>
        {
            if (++ticks != afterTicks || Content is not FrameworkElement root)
                return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var fs = System.IO.File.Create(pngPath))
                encoder.Save(fs);
            Close();
        };
    }

    private void Tick()
    {
        var frame = _session.Update(_pump.Latest);
        _mapView.Message = Readout.Message(frame);
        _readout.Text = string.Join("\n", Readout.Lines(_session)) + $"\ntrail    {_session.Trail.Count(p => p.HasValue)} points";
        _mapView.InvalidateVisual();
    }

    private Orientation CurrentOrientation => new(_flipX.IsChecked == true, _flipZ.IsChecked == true, _rotation);

    private void ApplyOrientation()
    {
        // One transform for map, markers, dot and trail: they all turn together.
        _session.SetOrientation(CurrentOrientation);
        _mapView.View.HasCentre = false;   // the old centre is in the previous orientation's pixels
        _saveStatus.Text = "Not saved yet.";
        _mapView.InvalidateVisual();
    }

    private void SaveOrientation()
    {
        try
        {
            ConfigFile.SaveOrientation(_configPath, CurrentOrientation);
            _saveStatus.Text = $"Saved {CurrentOrientation} to {_configPath}. Re-run the map builder to turn its PNG/SVG images the same way.";
        }
        catch (Exception ex)
        {
            _saveStatus.Text = "Could not save: " + ex.Message;
        }
    }

    private void UpdateRotationLabel() => _rotationLabel.Text = $"now {_rotation * 90}° clockwise";

    private void SyncControlsFromView()
    {
        _syncing = true;
        _follow.IsChecked = _mapView.View.Follow;
        _fit.IsChecked = _mapView.View.FitToWindow;
        _zoom.Value = Math.Log2(Math.Clamp(_mapView.View.Zoom, ViewState.MinZoom, ViewState.MaxZoom));
        _syncing = false;
    }

    private static CheckBox Check(string text, bool on) => new() { Content = text, IsChecked = on, Margin = new Thickness(0, 3, 0, 3) };

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };
}
