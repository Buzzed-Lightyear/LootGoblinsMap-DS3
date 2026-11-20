using System.Numerics;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

/// <summary>
/// The tracker's logic that needs neither a window nor the game: choosing the map, collected
/// markers, the test log, overlay placement, hotkeys and settings. Synthetic maps and names only.
/// </summary>
[TestClass]
public class TrackerLogicTests
{
    private static Vector3[] Quad(float x, float z, float size, float y) => new[]
    {
        new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y, z + size), new Vector3(x, y, z + size),
    };

    private static MapDocument.MarkerInfo Treasure(string name, string category, int flag, float x, float z, float y = 0) => new()
    {
        Type = MapDocument.TreasureType, Name = name, Layer = 0, World = new[] { x, y, z }, Flag = flag, Category = category,
        Items = { new MapDocument.ItemInfo { Name = name, Category = category } },
    };

    private static MapDocument.MarkerInfo Bonfire(string name, int flag, float x, float z, float y = 0) => new()
    {
        Type = MapDocument.BonfireType, Name = "obj", DisplayName = name, Layer = 0, World = new[] { x, y, z }, Flag = flag,
        Category = MapDocument.BonfireType,
    };

    /// <summary>A 40 × 8 m strip of floor starting at <paramref name="x0"/>, with markers.</summary>
    private static MapData Map(string id, string name, float x0, float y, params MapDocument.MarkerInfo[] markers)
    {
        var polys = Enumerable.Range(0, 10).Select(i => new NavPolygon(Quad(x0 + i * 4, 0, 8, y), 0)).ToList();
        var doc = new MapDocument { Map = id, Name = name, Markers = markers.ToList() };
        doc.SetTransform(MapTransform.FitBounds(x0, x0 + 44, 0, 8, 6.0, 3.0, false, false, 0));
        return MapData.Create("(memory)", doc, polys);
    }

    /// <summary>Two areas that meet at x = 40..44, as adjacent maps do.</summary>
    private static (MapData Keep, MapData Annex) TwoMaps() => (
        Map("m70_00_00_00", "Test Keep", 0, 0,
            Bonfire("Keep Gate", 17000001, 2, 4),
            Treasure("Test Key", ItemCategories.Key, 57000100, 10, 4),
            Treasure("Test Tonic", ItemCategories.Consumable, 57000110, 20, 4),
            new MapDocument.MarkerInfo { Type = MapDocument.TreasureType, Name = "leftover", Layer = -1, World = new[] { 0f, 0f, 0f }, Unused = true }),
        Map("m70_01_00_00", "Test Annex", 40, 0,
            Bonfire("Annex Hall", 17010001, 80, 4),
            Treasure("Test Ring", ItemCategories.Ring, 57010100, 60, 4)));

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    // ---- map switching ------------------------------------------------------------------

    [TestMethod]
    public void MapSwitch_FollowsTheGamesMapId()
    {
        var (keep, annex) = TwoMaps();
        var session = new TrackingSession(MapSet.Of(keep, annex), keep.Id, Orientation.Default);

        var f = session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), keep.Id), T0);
        Assert.AreEqual(TrackState.Tracking, f.State);
        Assert.AreSame(keep, session.Map);

        // In the strip both maps cover, the game's map id decides, not the position.
        session.Update(PositionSample.AtMap(new Vector3(42, 0, 4), keep.Id), T0.AddSeconds(1));
        Assert.AreSame(keep, session.Map);
        var crossed = session.Update(PositionSample.AtMap(new Vector3(42.5f, 0, 4), annex.Id), T0.AddSeconds(2));
        Assert.AreSame(annex, session.Map);
        Assert.AreEqual(TrackState.Tracking, crossed.State);
        Assert.AreEqual(annex.Id, crossed.ShownMapId);
        Assert.AreEqual(TrackingSession.MethodMapId, session.MapMethod);

        // The pixel position is in the new map's own image, and the trail starts afresh.
        var expected = annex.TransformFor(Orientation.Default).ToPixel(42.5, 4);
        Assert.AreEqual(expected.X, crossed.PixelX, 1e-9);
        Assert.AreEqual(1, session.Trail.Count);

        var events = session.DrainEvents();
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(TrackEventKind.MapSwitch, events[0].Kind);
        Assert.AreEqual("2026-01-01T12:00:02 map from=m70_00_00_00 to=m70_01_00_00 area=\"Test Annex\" method=\"map id read from memory\"",
                        LiveCheckLog.Format(events[0]));
        Assert.AreEqual(0, session.DrainEvents().Count, "events are handed out once");
    }

    [TestMethod]
    public void MapSwitch_UnbuiltMap_IsReportedAndTheShownMapStays()
    {
        var (keep, annex) = TwoMaps();
        var set = MapSet.Of(keep, annex);
        // The game has a third map that was not built (it has no navmesh, say).
        set.Index.KnownMapIds.AddRange(new[] { keep.Id, annex.Id, "m99_00_00_00" });
        var session = new TrackingSession(set, keep.Id, Orientation.Default);
        session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), keep.Id), T0);

        var f = session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), "m99_00_00_00"), T0.AddSeconds(1));
        Assert.AreEqual(TrackState.OutsideMap, f.State);
        Assert.AreEqual("m99_00_00_00", f.GameMapId);
        Assert.AreSame(keep, session.Map);
        StringAssert.Contains(f.Detail, "m99_00_00_00");
        Assert.AreEqual(0, session.DrainEvents().Count);

        // Back in a mapped area: tracking again.
        Assert.AreEqual(TrackState.Tracking, session.Update(PositionSample.AtMap(new Vector3(11, 0, 4), keep.Id), T0.AddSeconds(2)).State);
    }

    [TestMethod]
    public void MapSwitch_WithoutAMapId_FallsBackToPosition_OnlyWhenUnambiguous()
    {
        var (keep, annex) = TwoMaps();
        var set = MapSet.Of(keep, annex);
        var session = new TrackingSession(set, keep.Id, Orientation.Default);

        // On the shown map: stays, whatever else overlaps.
        session.Update(PositionSample.At(new Vector3(42, 0, 4)), T0);
        Assert.AreSame(keep, session.Map);

        // Far off the shown map and on exactly one other map: switch, and say how it was decided.
        session.Update(PositionSample.At(new Vector3(70, 0, 4)), T0.AddSeconds(1));
        Assert.AreSame(annex, session.Map);
        Assert.AreEqual(TrackingSession.MethodPosition, session.MapMethod);
        StringAssert.Contains(LiveCheckLog.Format(session.DrainEvents().Single()), "method=\"position (no map id available)\"");

        // A point both maps cover cannot be told apart by position.
        Assert.IsNull(set.LocateByPosition(new Vector3(42, 0, 4)));
        Assert.AreEqual(keep.Id, set.LocateByPosition(new Vector3(5, 0, 4)));
        Assert.IsNull(set.LocateByPosition(new Vector3(500, 0, 500)));
    }

    [TestMethod]
    public void MapIds_PackAndUnpackLikeTheGame()
    {
        Assert.AreEqual("m30_00_00_00", MapIds.FromPacked(0x1E000000));
        Assert.AreEqual("m51_01_00_00", MapIds.FromPacked(0x33010000));
        Assert.IsTrue(MapIds.TryPack("m30_01_00_00", out uint packed));
        Assert.AreEqual(0x1E010000u, packed);
        Assert.IsTrue(MapIds.TryParse("m34_01_00_00", out int area, out int block));
        Assert.AreEqual((34, 1), (area, block));
        foreach (string bad in new[] { null, "", "m30_00", "x30_00_00_00", "m3a_00_00_00", "m30-00-00-00" })
            Assert.IsFalse(MapIds.TryParse(bad, out _, out _), bad);
    }

    // ---- collected markers --------------------------------------------------------------

    [TestMethod]
    public void FlagFlip_HidesTheMarker_AndIsLogged()
    {
        var (keep, annex) = TwoMaps();
        var session = new TrackingSession(MapSet.Of(keep, annex), keep.Id, Orientation.Default);
        var settings = new TrackerSettings();
        var key = keep.Markers[1];
        var tonic = keep.Markers[2];
        session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), keep.Id), T0);

        CollectionAssert.AreEquivalent(new[] { 17000001, 57000100, 57000110 }, session.WatchedFlags());
        Assert.AreEqual(MarkerVisibility.Shown, session.VisibilityOf(key, settings), "before any flag is read, nothing is hidden");
        Assert.AreEqual((2, 2), session.TreasureCount());

        // First reading: the tonic was picked up in an earlier session. Recorded, not logged.
        session.ApplyFlags(new Dictionary<int, bool> { [17000001] = true, [57000100] = false, [57000110] = true }, T0.AddSeconds(1));
        Assert.AreEqual(0, session.DrainEvents().Count);
        Assert.AreEqual(MarkerVisibility.Shown, session.VisibilityOf(key, settings));
        Assert.AreEqual(MarkerVisibility.Hidden, session.VisibilityOf(tonic, settings), "items already collected are not shown");
        Assert.AreEqual((1, 2), session.TreasureCount());

        // The key is picked up now: its flag flips, the marker goes away, the log gets a line.
        session.ApplyFlags(new Dictionary<int, bool> { [17000001] = true, [57000100] = true, [57000110] = true }, T0.AddSeconds(2));
        Assert.AreEqual(MarkerVisibility.Hidden, session.VisibilityOf(key, settings));
        Assert.AreEqual((0, 2), session.TreasureCount());
        var flip = session.DrainEvents().Single();
        Assert.AreEqual("2026-01-01T12:00:02 flag map=m70_00_00_00 area=\"Test Keep\" item=\"Test Key\" flag=57000100 state=on", LiveCheckLog.Format(flip));

        // The same reading again is not another flip.
        session.ApplyFlags(new Dictionary<int, bool> { [57000100] = true }, T0.AddSeconds(3));
        Assert.AreEqual(0, session.DrainEvents().Count);

        // "Show collected" brings them back dimmed.
        settings.ShowCollected = true;
        Assert.AreEqual(MarkerVisibility.Dimmed, session.VisibilityOf(key, settings));
        Assert.AreEqual(MarkerVisibility.Dimmed, session.VisibilityOf(tonic, settings));
    }

    [TestMethod]
    public void Visibility_FollowsCategoryFilters_AndNeverShowsUnusedOrUnplacedMarkers()
    {
        var (keep, _) = TwoMaps();
        var session = new TrackingSession(keep, Orientation.Default);
        var settings = new TrackerSettings();

        Assert.AreEqual(MarkerVisibility.Shown, session.VisibilityOf(keep.Markers[0], settings));
        Assert.AreEqual(MarkerVisibility.Hidden, session.VisibilityOf(keep.Markers[3], settings), "an unused treasure event");

        settings.SetCategoryShown(ItemCategories.Consumable, false);
        Assert.AreEqual(MarkerVisibility.Hidden, session.VisibilityOf(keep.Markers[2], settings));
        Assert.AreEqual(MarkerVisibility.Shown, session.VisibilityOf(keep.Markers[1], settings));
        settings.SetCategoryShown(ItemCategories.Consumable, true);
        Assert.AreEqual(MarkerVisibility.Shown, session.VisibilityOf(keep.Markers[2], settings));

        settings.ShowBonfires = false;
        Assert.AreEqual(MarkerVisibility.Hidden, session.VisibilityOf(keep.Markers[0], settings));

        Assert.AreEqual("Keep Gate", keep.Markers[0].Label);
        Assert.AreEqual("Test Key", keep.Markers[1].Label);
    }

    // ---- the test log -------------------------------------------------------------------

    [TestMethod]
    public void BonfireRest_IsLoggedOncePerVisit()
    {
        var (keep, annex) = TwoMaps();
        var session = new TrackingSession(MapSet.Of(keep, annex), keep.Id, Orientation.Default);
        var at = new Vector3(2.6f, 0, 4.4f);   // 0.72 m from the bonfire marker at (2, 0, 4)
        var time = T0;

        // Walking up: no log while moving.
        for (int i = 0; i < 10; i++, time = time.AddMilliseconds(40))
            session.Update(PositionSample.AtMap(new Vector3(8 - i * 0.5f, 0, 4.4f), keep.Id), time);
        Assert.AreEqual(0, session.DrainEvents().Count);

        // Standing still for two seconds: one line, with the distance.
        for (int i = 0; i < 50; i++, time = time.AddMilliseconds(40))
            session.Update(PositionSample.AtMap(at, keep.Id), time);
        var rest = session.DrainEvents().Single();
        Assert.AreEqual(TrackEventKind.BonfireRest, rest.Kind);
        StringAssert.Contains(LiveCheckLog.Format(rest), "bonfire map=m70_00_00_00 area=\"Test Keep\" bonfire=\"Keep Gate\" distance=0.72");

        // Still sitting there: nothing more. Shuffling about next to it: nothing more.
        for (int i = 0; i < 50; i++, time = time.AddMilliseconds(40))
            session.Update(PositionSample.AtMap(i < 25 ? at : at + new Vector3(0.3f, 0, 0), keep.Id), time);
        Assert.AreEqual(0, session.DrainEvents().Count);

        // Standing still somewhere with no bonfire near: no line.
        for (int i = 0; i < 50; i++, time = time.AddMilliseconds(40))
            session.Update(PositionSample.AtMap(new Vector3(30, 0, 4), keep.Id), time);
        Assert.AreEqual(0, session.DrainEvents().Count);

        // Coming back and resting again: a second line.
        for (int i = 0; i < 50; i++, time = time.AddMilliseconds(40))
            session.Update(PositionSample.AtMap(at, keep.Id), time);
        Assert.AreEqual(1, session.DrainEvents().Count);
    }

    [TestMethod]
    public void LiveCheckLog_AppendsLinesToTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "lgm-log-" + Guid.NewGuid().ToString("N"), "live_checks.log");
        try
        {
            var log = new LiveCheckLog(path);
            log.Append(Array.Empty<TrackEvent>());
            Assert.IsFalse(File.Exists(path), "nothing to say, nothing written");
            log.Append(new[] { new TrackEvent(T0, TrackEventKind.BonfireRest, "m70_00_00_00", "Test Keep", "Keep Gate", "distance=0.10") });
            log.Append(new[] { new TrackEvent(T0.AddSeconds(5), TrackEventKind.FlagFlip, "m70_00_00_00", "Test Keep", "Test Key", "flag=1 state=on") });
            var lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            StringAssert.StartsWith(lines[0], "2026-01-01T12:00:00 bonfire ");
            StringAssert.StartsWith(lines[1], "2026-01-01T12:00:05 flag ");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---- replay scripts -----------------------------------------------------------------

    [TestMethod]
    public void ReplayScript_CarriesMapIdsAndFlagChanges()
    {
        var script = ReplayScript.Parse(new[]
        {
            "# a comment",
            "@flag 57000110 on",
            "@map m70_00_00_00",
            "1,0,4",
            "2,0,4",
            "@flag 57000100 on",
            "3,0,4",
            "@map m70_01_00_00",
            "0.04, 42,0,4",
            "@map none",
            "43,0,4",
        });

        Assert.AreEqual(5, script.Steps.Count);
        CollectionAssert.AreEqual(new Dictionary<int, bool> { [57000110] = true }, script.InitialFlags);
        Assert.AreEqual("m70_00_00_00", script.Steps[0].MapId);
        Assert.AreEqual(0, script.Steps[1].FlagChanges.Count);
        CollectionAssert.AreEqual(new Dictionary<int, bool> { [57000100] = true }, script.Steps[2].FlagChanges);
        Assert.AreEqual("m70_01_00_00", script.Steps[3].MapId);
        Assert.AreEqual(new Vector3(42, 0, 4), script.Steps[3].Position);
        Assert.IsNull(script.Steps[4].MapId);

        // Written out and read back, it is the same script.
        string path = Path.Combine(Path.GetTempPath(), "lgm-replay-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            script.Write(path, "header line");
            var again = ReplayScript.Read(path);
            Assert.AreEqual(script.Steps.Count, again.Steps.Count);
            CollectionAssert.AreEqual(script.InitialFlags, again.InitialFlags);
            for (int i = 0; i < script.Steps.Count; i++)
            {
                Assert.AreEqual(script.Steps[i].Position, again.Steps[i].Position);
                Assert.AreEqual(script.Steps[i].MapId, again.Steps[i].MapId);
                CollectionAssert.AreEqual(script.Steps[i].FlagChanges, again.Steps[i].FlagChanges);
            }
        }
        finally
        {
            File.Delete(path);
        }

        Assert.ThrowsException<InvalidDataException>(() => ReplayScript.Parse(new[] { "@map thirty" }));
        Assert.ThrowsException<InvalidDataException>(() => ReplayScript.Parse(new[] { "@flag 12 maybe" }));
        Assert.ThrowsException<InvalidDataException>(() => ReplayScript.Parse(new[] { "@teleport 1 2 3" }));
    }

    // ---- overlay placement --------------------------------------------------------------

    [TestMethod]
    public void Overlay_CoversExactlyTheGamesClientArea_WhileTheGameIsInFront()
    {
        var client = new PixelRect(120, 80, 2560, 1440);
        var playing = new GameWindowState(true, client, Minimized: false, Foreground: true);

        var (visible, rect) = OverlayLayout.Place(playing);
        Assert.IsTrue(visible);
        Assert.AreEqual(client, rect);

        // Moved or resized: the overlay rectangle is simply the new client rectangle.
        var moved = playing with { Client = new PixelRect(-1920, 0, 1920, 1080) };
        Assert.AreEqual(moved.Client, OverlayLayout.Place(moved).Rect);

        // Alt-tabbed away, minimized, or gone: hidden.
        Assert.IsFalse(OverlayLayout.Place(playing with { Foreground = false }).Visible);
        Assert.IsFalse(OverlayLayout.Place(playing with { Minimized = true }).Visible);
        Assert.IsFalse(OverlayLayout.Place(playing with { Client = new PixelRect(0, 0, 0, 0) }).Visible);
        Assert.IsFalse(OverlayLayout.Place(GameWindowState.Missing).Visible);

        // The tracker's own settings window in front does not make the overlay vanish.
        Assert.IsTrue(OverlayLayout.Place(playing with { Foreground = false }, trackerInForeground: true).Visible);
        Assert.IsFalse(OverlayLayout.Place(playing with { Minimized = true }, trackerInForeground: true).Visible);
    }

    [TestMethod]
    public void Overlay_MinimapSitsInItsCorner_AndTheFullMapIsCentred()
    {
        Assert.AreEqual(new PixelRect(1920 - 24 - 340, 24, 340, 340), OverlayLayout.Minimap(1920, 1080, Corner.TopRight, 340, 24));
        Assert.AreEqual(new PixelRect(24, 24, 340, 340), OverlayLayout.Minimap(1920, 1080, Corner.TopLeft, 340, 24));
        Assert.AreEqual(new PixelRect(24, 1080 - 24 - 340, 340, 340), OverlayLayout.Minimap(1920, 1080, Corner.BottomLeft, 340, 24));
        Assert.AreEqual(new PixelRect(1920 - 24 - 340, 1080 - 24 - 340, 340, 340), OverlayLayout.Minimap(1920, 1080, Corner.BottomRight, 340, 24));

        // A small window: the minimap shrinks to fit instead of hanging over the edge.
        var small = OverlayLayout.Minimap(400, 300, Corner.BottomRight, 340, 24);
        Assert.AreEqual(252, small.Width);
        Assert.IsTrue(small.X >= 0 && small.Y >= 0 && small.Right <= 400 && small.Bottom <= 300);

        var full = OverlayLayout.FullMap(1920, 1080, 0.86);
        Assert.AreEqual(1651, full.Width);
        Assert.AreEqual(929, full.Height);
        Assert.AreEqual((1920 - 1651) / 2, full.X);
        Assert.AreEqual((1080 - 929) / 2, full.Y);
    }

    // ---- hotkeys and settings -----------------------------------------------------------

    [TestMethod]
    public void Hotkey_ParsesModifiersAndKeys()
    {
        Assert.IsTrue(Hotkey.TryParse("F7", out var f7, out _));
        Assert.AreEqual((0x76, 0u), (f7.VirtualKey, f7.Modifiers));
        Assert.AreEqual("F7", f7.ToString());

        Assert.IsTrue(Hotkey.TryParse("ctrl + shift+m", out var m, out _));
        Assert.AreEqual((int)'M', m.VirtualKey);
        Assert.AreEqual(Hotkey.ModCtrl | Hotkey.ModShift, m.Modifiers);
        Assert.AreEqual("Ctrl+Shift+M", m.ToString());

        Assert.IsTrue(Hotkey.TryParse("Ctrl+PageUp", out var up, out _));
        Assert.AreEqual(0x21, up.VirtualKey);
        Assert.IsTrue(Hotkey.TryParse("Alt+Win+Num5", out var num, out _));
        Assert.AreEqual((0x65, Hotkey.ModAlt | Hotkey.ModWin), (num.VirtualKey, num.Modifiers));

        foreach (string bad in new[] { "", "Ctrl+", "Ctrl+Shift", "F7+F8", "Ctrl+Banana", "Hyper+F7", null })
        {
            Assert.IsFalse(Hotkey.TryParse(bad, out _, out string error), bad);
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }
    }

    [TestMethod]
    public void ControllerChord_NeedsTwoButtons_AndMatchesOnlyWhenAllAreHeld()
    {
        Assert.IsTrue(ControllerChord.TryParse("back + rightthumb", out var chord, out _));
        Assert.AreEqual("Back+RightThumb", chord.Text);
        Assert.AreEqual((ushort)0x00A0, chord.Mask);
        Assert.IsTrue(chord.IsHeld(0x00A0));
        Assert.IsTrue(chord.IsHeld(0x10A0), "other buttons held as well do not matter");
        Assert.IsFalse(chord.IsHeld(0x0020), "one of the two is not the chord");
        Assert.IsFalse(chord.IsHeld(0));

        Assert.IsFalse(ControllerChord.TryParse("A", out _, out _), "a single button would fire on every press in the game");
        Assert.IsFalse(ControllerChord.TryParse("A+A", out _, out _));
        Assert.IsFalse(ControllerChord.TryParse("Back+Turbo", out _, out _));
        Assert.IsFalse(default(ControllerChord).IsHeld(0xFFFF));
    }

    [TestMethod]
    public void Settings_DefaultsAvoidTheGamesKeys_AndTheChordIsOff()
    {
        var settings = new TrackerSettings();
        settings.Validate();
        Assert.AreEqual(0, settings.Problems.Count);
        Assert.IsFalse(settings.ControllerChordEnabled);
        Assert.IsFalse(settings.ShowCollected, "collected markers are hidden by default");

        // No default is a bare letter, digit, arrow or other key the game binds for play.
        foreach (string text in new[] { settings.FullMapHotkey, settings.LayerUpHotkey, settings.LayerDownHotkey, settings.SettingsHotkey, settings.ToggleOverlayHotkey, settings.MarkFeedbackHotkey })
        {
            Assert.IsTrue(Hotkey.TryParse(text, out var hotkey, out _), text);
            bool functionKey = hotkey.VirtualKey is >= 0x70 and <= 0x87;
            Assert.IsTrue(functionKey || hotkey.Modifiers != 0, $"{text} would take a plain key away from the game");
            Assert.AreNotEqual(0x7B, hotkey.VirtualKey, "F12 is the launcher's screenshot key");
        }
    }

    [TestMethod]
    public void Settings_ParseKeepsWhatIsValid_AndFallsBackForTheRest()
    {
        var settings = TrackerSettings.Parse("""
            {
              // hand-edited
              "hiddenCategories": ["Consumable", "souls", "soul", "nonsense"],
              "showCollected": true,
              "minimapCorner": "BottomLeft",
              "minimapSize": 99999,
              "minimapOpacity": 0.01,
              "minimapPixelsPerMeter": 7.5,
              "minimapLabels": -3,
              "fullMapHotkey": "ctrl+m",
              "layerUpHotkey": "Banana",
              "settingsHotkey": "Ctrl+M",
              "controllerChordEnabled": true,
              "fullMapControllerChord": "Start",
              "somethingFromTheFuture": 1,
            }
            """);

        Assert.IsTrue(settings.ShowCollected);
        Assert.AreEqual(Corner.BottomLeft, settings.MinimapCorner);
        Assert.AreEqual(1200, settings.MinimapSize);
        Assert.AreEqual(0.2, settings.MinimapOpacity);
        Assert.AreEqual(7.5, settings.MinimapPixelsPerMeter);
        Assert.AreEqual(0, settings.MinimapLabels);
        CollectionAssert.AreEqual(new[] { "consumable", "soul" }, settings.HiddenCategories);
        Assert.IsFalse(settings.IsCategoryShown(ItemCategories.Soul));
        Assert.IsTrue(settings.IsCategoryShown(ItemCategories.Key));

        Assert.AreEqual("Ctrl+M", settings.FullMapHotkey);
        Assert.AreEqual(new TrackerSettings().LayerUpHotkey, settings.LayerUpHotkey, "an unusable shortcut falls back to its default");
        Assert.AreEqual(new TrackerSettings().FullMapControllerChord, settings.FullMapControllerChord);
        Assert.IsTrue(settings.Problems.Any(p => p.Contains("LayerUpHotkey")));
        Assert.IsTrue(settings.Problems.Any(p => p.Contains("FullMapHotkey") && p.Contains("SettingsHotkey")), "two actions on one shortcut is reported");
        Assert.IsTrue(settings.Problems.Any(p => p.Contains("FullMapControllerChord")));
        Assert.AreEqual("parchment", settings.Theme, "the themed look is the default");
        Assert.AreEqual("plain", TrackerSettings.Parse("""{ "theme": "plain" }""").Theme);
        Assert.AreEqual("parchment", TrackerSettings.Parse("""{ "theme": "a/b:c" }""").Theme, "a theme name is a file name; anything else falls back");

        // Broken JSON never stops the tracker: defaults, with the reason.
        var broken = TrackerSettings.Parse("{ this is not json");
        Assert.AreEqual(new TrackerSettings().FullMapHotkey, broken.FullMapHotkey);
        Assert.AreEqual(1, broken.Problems.Count);
    }

    [TestMethod]
    public void Settings_SaveAndLoad_RoundTrip_AndAMissingFileMeansDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), "lgm-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            Assert.AreEqual(new TrackerSettings().MinimapSize, TrackerSettings.Load(path).MinimapSize);

            var settings = new TrackerSettings
            {
                ShowCollected = true, MinimapCorner = Corner.BottomRight, MinimapSize = 420, MinimapOpacity = 0.6,
                FullMapHotkey = "Ctrl+Shift+M", ControllerChordEnabled = true, FullMapControllerChord = "LeftThumb+RightThumb",
            };
            settings.SetCategoryShown(ItemCategories.Upgrade, false);
            settings.Save(path);

            var loaded = TrackerSettings.Load(path);
            Assert.AreEqual(0, loaded.Problems.Count);
            Assert.AreEqual(settings.ToJson(), loaded.ToJson());
            Assert.IsFalse(loaded.IsCategoryShown(ItemCategories.Upgrade));
            Assert.AreEqual(Corner.BottomRight, loaded.MinimapCorner);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---- map index ----------------------------------------------------------------------

    [TestMethod]
    public void MapIndex_RoundTrips_AndTheSetLoadsMapsOnDemand()
    {
        string root = Path.Combine(Path.GetTempPath(), "lgm-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (keep, annex) = TwoMaps();
            foreach (var map in new[] { keep, annex })
            {
                string dir = Path.Combine(root, map.Id);
                Directory.CreateDirectory(dir);
                map.Document.NavPolygons = NavPolyFile.FileName;
                map.Document.Save(Path.Combine(dir, MapDocument.FileName));
                NavPolyFile.Write(Path.Combine(dir, NavPolyFile.FileName), map.Polygons.ToList());
            }
            var index = new MapIndex { Language = "engus", MapsOverlapInSpace = true };
            index.Maps.Add(new MapIndex.Entry { Id = keep.Id, Name = keep.Name, Directory = keep.Id, Built = true, ChecksPassed = true });
            index.Maps.Add(new MapIndex.Entry { Id = annex.Id, Name = annex.Name, Directory = annex.Id, Built = true, ChecksPassed = true });
            index.Maps.Add(new MapIndex.Entry { Id = "m70_02_00_00", Built = false, Problem = "no navmesh" });
            index.Save(Path.Combine(root, MapIndex.FileName));

            var set = MapSet.Load(root, keep.Id);
            CollectionAssert.AreEqual(new[] { keep.Id, annex.Id }, set.Ids.ToArray());
            Assert.IsTrue(set.Index.MapsOverlapInSpace);
            Assert.AreEqual("Test Annex", set.Get(annex.Id).Name);
            Assert.AreEqual(3, set.Get(keep.Id).Markers.Count(m => !m.Unused));
            Assert.AreEqual("Test Key", set.Get(keep.Id).Markers[1].Items[0].Name, "item details survive the trip through map.json");
            Assert.IsNull(set.Get("m70_02_00_00"), "a map that was not built");
            Assert.IsNull(set.Get("m99_00_00_00"));
            Assert.IsFalse(set.Has("m70_02_00_00"));
            Assert.AreEqual("m70_02_00_00", set.NameOf("m70_02_00_00"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
