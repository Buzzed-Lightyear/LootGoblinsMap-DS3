using System.Windows;
using System.Windows.Media;
using MapCore;
using Tracker.Rendering;

namespace Tracker.Overlay;

/// <summary>What the overlay is showing right now.</summary>
internal sealed class OverlayState
{
    public bool FullMap;
    /// <summary>Layer chosen with the layer keys while the full map is open; null follows the player.</summary>
    public int? ShownLayer;
    /// <summary>A line shown briefly at the bottom (for example "settings saved").</summary>
    public string? Notice;
}

/// <summary>
/// Draws the overlay's content: the minimap in a corner, or the large centred full map. It is
/// the only thing in the transparent overlay window, so everything outside the panel stays clear.
/// </summary>
internal sealed class OverlayView : FrameworkElement
{
    private readonly TrackerCore _core;
    private readonly ViewState _miniView = new() { Follow = true };
    private readonly ViewState _fullView = new() { FitToWindow = true };

    public OverlayState State { get; } = new();

    public OverlayView(TrackerCore core)
    {
        _core = core;
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>A new map or orientation: the follow centre is in pixels that no longer apply.</summary>
    public void ResetView() => _miniView.HasCentre = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Draw(dc, _core, State, _miniView, _fullView, ActualWidth, ActualHeight, dip);
    }

    /// <summary>The drawing itself, shared with the PNG export so that what is tested is what is shown.</summary>
    public static void Draw(DrawingContext dc, TrackerCore core, OverlayState state, ViewState miniView, ViewState fullView,
                            double width, double height, double pixelsPerDip)
    {
        var session = core.Session;
        var settings = core.Settings;
        var map = session.Map;

        if (state.FullMap)
        {
            var rect = OverlayLayout.FullMap((int)width, (int)height);
            int playerLayer = session.Current.State == TrackState.Tracking ? session.Current.Layer : session.LastLayer;
            int shown = state.ShownLayer ?? playerLayer;
            var (left, total) = session.TreasureCount();
            string layerText = shown >= 0
                ? $"layer {shown + 1} of {map.LayerCount}" + (state.ShownLayer.HasValue && shown != playerLayer ? " (you are on " + (playerLayer >= 0 ? (playerLayer + 1).ToString() : "-") + ")" : "")
                : $"{map.LayerCount} layers";
            var style = new RenderStyle
            {
                Opaque = false,
                PanelOpacity = 0.95,
                ShownLayer = state.ShownLayer,
                LabelCount = -1,
                Legend = true,
                Settings = settings,
                Title = $"{map.Name}  ·  {layerText}",
                Footer = $"{left} of {total} items left   ·   {settings.FullMapHotkey} close   ·   {settings.LayerUpHotkey} / {settings.LayerDownHotkey} layer   ·   {settings.SettingsHotkey} settings" +
                         (state.Notice != null ? "   ·   " + state.Notice : ""),
            };
            core.Renderer.Render(dc, new Rect(rect.X, rect.Y, rect.Width, rect.Height), session, fullView, style,
                                 Readout.Message(session, brief: true), null, pixelsPerDip);
            return;
        }

        var mini = OverlayLayout.Minimap((int)width, (int)height, settings.MinimapCorner, settings.MinimapSize, settings.MinimapMargin);
        // Zoom is given in screen pixels per metre; the map's own pixels are Scale per metre.
        miniView.Zoom = settings.MinimapPixelsPerMeter / session.Transform.Scale;
        miniView.Follow = true;
        var miniStyle = new RenderStyle
        {
            Opaque = false,
            PanelOpacity = settings.MinimapOpacity,
            LabelCount = settings.MinimapLabels,
            Settings = settings,
            Title = map.Name,
            Footer = state.Notice,
        };
        core.Renderer.Render(dc, new Rect(mini.X, mini.Y, mini.Width, mini.Height), session, miniView, miniStyle,
                             Readout.Message(session, brief: true), null, pixelsPerDip);
    }
}
