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

    private string? _drawnSignature;
    private PixelRect _panel;

    /// <summary>
    /// Places this element on the panel it draws (the minimap, or the full map) inside an overlay of
    /// the given size. Only the panel's own area is ever redrawn; the rest of the transparent
    /// window is left alone, which is what keeps the overlay cheap.
    /// </summary>
    public void PlaceIn(double overlayWidth, double overlayHeight)
    {
        var panel = PanelRect(_core, State, overlayWidth, overlayHeight);
        if (panel == _panel)
            return;
        _panel = panel;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(panel.X, panel.Y, 0, 0);
        Width = panel.Width;
        Height = panel.Height;
        _drawnSignature = null;
    }

    /// <summary>Redraws only when what the panel shows has changed since it was last drawn.</summary>
    public bool RedrawIfChanged()
    {
        var session = _core.Session;
        var f = session.Current;
        // Positions are compared at a quarter of a screen pixel: standing still costs nothing.
        string signature = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{session.Map.Id}|{session.Revision}|{f.State}|{f.Layer}|{Math.Round(f.PixelX * 4)}|{Math.Round(f.PixelY * 4)}|{session.Trail.Count}|{State.FullMap}|{State.ShownLayer}|{State.Notice}|{_panel}|{_core.SettingsVersion}");
        if (signature == _drawnSignature)
            return false;
        _drawnSignature = signature;
        InvalidateVisual();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1)
            return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawPanel(dc, _core, State, _miniView, _fullView, new Rect(0, 0, ActualWidth, ActualHeight), dip);
    }

    /// <summary>Where the panel goes inside an overlay of the given size: the corner minimap, or the centred full map.</summary>
    public static PixelRect PanelRect(TrackerCore core, OverlayState state, double width, double height)
    {
        var settings = core.Settings;
        return state.FullMap
            ? OverlayLayout.FullMap((int)width, (int)height)
            : OverlayLayout.Minimap((int)width, (int)height, settings.MinimapCorner, settings.MinimapSize, settings.MinimapMargin);
    }

    /// <summary>Draws the panel at its place in a whole overlay (the PNG export).</summary>
    public static void Draw(DrawingContext dc, TrackerCore core, OverlayState state, ViewState miniView, ViewState fullView,
                            double width, double height, double pixelsPerDip)
    {
        var panel = PanelRect(core, state, width, height);
        DrawPanel(dc, core, state, miniView, fullView, new Rect(panel.X, panel.Y, panel.Width, panel.Height), pixelsPerDip);
    }

    /// <summary>The drawing itself, shared with the PNG export so that what is tested is what is shown.</summary>
    public static void DrawPanel(DrawingContext dc, TrackerCore core, OverlayState state, ViewState miniView, ViewState fullView,
                                 Rect panel, double pixelsPerDip)
    {
        var session = core.Session;
        var settings = core.Settings;
        var map = session.Map;

        if (state.FullMap)
        {
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
            core.Renderer.Render(dc, panel, session, fullView, style, Readout.Message(session, brief: true), null, pixelsPerDip);
            return;
        }

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
        core.Renderer.Render(dc, panel, session, miniView, miniStyle, Readout.Message(session, brief: true), null, pixelsPerDip);
    }
}
