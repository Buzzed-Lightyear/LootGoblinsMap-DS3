using System.Numerics;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

/// <summary>Tests for the shared library: polygon/layer lookup, polygon file, orientation, config saving, tracking.</summary>
[TestClass]
public class CoreTests
{
    private static Vector3[] Quad(float x, float z, float size, float y = 0) => new[]
    {
        new Vector3(x, y, z),
        new Vector3(x + size, y, z),
        new Vector3(x + size, y, z + size),
        new Vector3(x, y, z + size),
    };

    /// <summary>A 4 m quad that rises from y0 at its low-x edge to y1 at its high-x edge.</summary>
    private static Vector3[] Ramp(float x, float z, float y0, float y1) => new[]
    {
        new Vector3(x, y0, z),
        new Vector3(x + 4, y1, z),
        new Vector3(x + 4, y1, z + 4),
        new Vector3(x, y0, z + 4),
    };

    // ---- polygon and layer lookup -------------------------------------------------------

    [TestMethod]
    public void Locate_StackedFloors_PicksSmallestVerticalDistance()
    {
        // Two floors over the same spot, 5 m apart, on different layers.
        var locator = new PolygonLocator(new[]
        {
            new NavPolygon(Quad(0, 0, 4, 0), layer: 0),
            new NavPolygon(Quad(0, 0, 4, 5), layer: 1),
        });

        var low = locator.Locate(new Vector3(2, 0.3f, 2));
        Assert.AreEqual(LocateKind.Inside, low.Kind);
        Assert.AreEqual(0, low.Layer);
        Assert.AreEqual(0.3f, low.Distance, 1e-4f);

        var high = locator.Locate(new Vector3(2, 4.6f, 2));
        Assert.AreEqual(LocateKind.Inside, high.Kind);
        Assert.AreEqual(1, high.Layer);
        Assert.AreEqual(0.4f, high.Distance, 1e-4f);
    }

    [TestMethod]
    public void Locate_LayerComesFromThePolygon_NotFromHeight()
    {
        // Layers are stack order: here the higher surface is on the lower layer index elsewhere on the map.
        var locator = new PolygonLocator(new[]
        {
            new NavPolygon(Quad(0, 0, 4, 10), layer: 0),     // a lone high floor: lowest surface at its spot
            new NavPolygon(Quad(20, 0, 4, 0), layer: 0),
            new NavPolygon(Quad(20, 0, 4, 6), layer: 1),     // lower than the lone floor, yet layer 1
        });

        Assert.AreEqual(0, locator.Locate(new Vector3(2, 10, 2)).Layer);
        Assert.AreEqual(1, locator.Locate(new Vector3(22, 6, 2)).Layer);
    }

    [TestMethod]
    public void Locate_OnARamp_MeasuresToTheSlopedSurface()
    {
        var locator = new PolygonLocator(new[] { new NavPolygon(Ramp(0, 0, 0, 4), layer: 0) });
        // Three quarters of the way up the ramp the surface is at y = 3.
        var hit = locator.Locate(new Vector3(3, 3.1f, 2));
        Assert.AreEqual(LocateKind.Inside, hit.Kind);
        Assert.AreEqual(0.1f, hit.Distance, 1e-3f);
    }

    [TestMethod]
    public void Locate_NotInsideAny_TakesNearestWithinThreeMetres()
    {
        var locator = new PolygonLocator(new[]
        {
            new NavPolygon(Quad(0, 0, 4, 0), layer: 2),
            new NavPolygon(Quad(10, 0, 4, 0), layer: 5),
        });

        // 1 m off the first quad's edge: nearest, layer 2.
        var near = locator.Locate(new Vector3(5, 0, 2));
        Assert.AreEqual(LocateKind.Nearest, near.Kind);
        Assert.AreEqual(2, near.Layer);
        Assert.AreEqual(1f, near.Distance, 1e-4f);

        // 2 m from the second quad and 4 m from the first: nearest is the second.
        Assert.AreEqual(5, locator.Locate(new Vector3(8, 0, 2)).Layer);

        // 3.5 m beyond the last edge: out of reach.
        var far = locator.Locate(new Vector3(17.5f, 0, 2));
        Assert.AreEqual(LocateKind.None, far.Kind);
        Assert.IsFalse(far.Found);
        Assert.AreEqual(-1, far.Layer);
    }

    [TestMethod]
    public void Locate_NearestDistanceIsThreeDimensional()
    {
        var locator = new PolygonLocator(new[] { new NavPolygon(Quad(0, 0, 4, 0), layer: 0) });
        // 2 m sideways and 2 m up from the edge: 2.83 m away, still within reach.
        var hit = locator.Locate(new Vector3(6, 2, 2));
        Assert.AreEqual(LocateKind.Nearest, hit.Kind);
        Assert.AreEqual(MathF.Sqrt(8), hit.Distance, 1e-3f);
        // 2 m sideways and 3 m up: 3.6 m away, out of reach.
        Assert.IsFalse(locator.Locate(new Vector3(6, 3, 2)).Found);
    }

    [TestMethod]
    public void Locate_FarAboveAFloor_PrefersAPolygonWithinReach()
    {
        // Standing on a ledge without navmesh, 20 m above a floor, with a walkway 1 m to the side.
        var locator = new PolygonLocator(new[]
        {
            new NavPolygon(Quad(0, 0, 4, 0), layer: 0),
            new NavPolygon(Quad(4.5f, 0, 4, 20), layer: 3),
        });
        var hit = locator.Locate(new Vector3(3.5f, 20, 2));
        Assert.AreEqual(LocateKind.Nearest, hit.Kind);
        Assert.AreEqual(3, hit.Layer);
        Assert.AreEqual(1f, hit.Distance, 1e-4f);
    }

    [TestMethod]
    public void Locate_NothingInReach_FallsBackToTheContainingPolygon()
    {
        var locator = new PolygonLocator(new[] { new NavPolygon(Quad(0, 0, 4, 0), layer: 4) });
        var hit = locator.Locate(new Vector3(2, 30, 2));
        Assert.AreEqual(LocateKind.Inside, hit.Kind);
        Assert.AreEqual(4, hit.Layer);
        Assert.AreEqual(30f, hit.Distance, 1e-3f);
    }

    [TestMethod]
    public void Locate_NonFinitePosition_IsNotFound()
    {
        var locator = new PolygonLocator(new[] { new NavPolygon(Quad(0, 0, 4, 0), layer: 0) });
        Assert.IsFalse(locator.Locate(new Vector3(float.NaN, 0, 0)).Found);
        Assert.IsFalse(locator.Locate(new Vector3(0, float.PositiveInfinity, 0)).Found);
    }

    [TestMethod]
    public void Locate_AgreesWithBruteForce_OnARandomMesh()
    {
        // The spatial grid must not change the answer: compare with a scan over every polygon.
        var rng = new Random(7);
        var polys = new List<NavPolygon>();
        for (int i = 0; i < 400; i++)
        {
            float x = (float)(rng.NextDouble() * 200 - 100), z = (float)(rng.NextDouble() * 200 - 100);
            polys.Add(new NavPolygon(Quad(x, z, 1 + (float)rng.NextDouble() * 9, (float)(rng.NextDouble() * 30)), rng.Next(0, 6)));
        }
        var locator = new PolygonLocator(polys);

        for (int n = 0; n < 2000; n++)
        {
            var p = new Vector3((float)(rng.NextDouble() * 220 - 110), (float)(rng.NextDouble() * 34 - 2), (float)(rng.NextDouble() * 220 - 110));
            var hit = locator.Locate(p);

            int bestInside = -1; float bestInsideDist = float.MaxValue;
            int bestNear = -1; float bestNearDist = float.MaxValue;
            for (int i = 0; i < polys.Count; i++)
            {
                if (Geometry2D.PointInPolyXZ(p, polys[i].Vertices))
                {
                    float d = MathF.Abs(p.Y - Geometry2D.HeightAtXZ(polys[i].Vertices, p.X, p.Z));
                    if (d < bestInsideDist) { bestInsideDist = d; bestInside = i; }
                }
                float o = Geometry2D.DistancePointToOutline3D(p, polys[i].Vertices);
                if (o < bestNearDist) { bestNearDist = o; bestNear = i; }
            }

            if (bestInside >= 0 && bestInsideDist <= 3f)
            {
                Assert.AreEqual(LocateKind.Inside, hit.Kind);
                Assert.AreEqual(bestInsideDist, hit.Distance, 1e-4f);
            }
            else if (bestNear >= 0 && bestNearDist <= 3f)
            {
                Assert.AreEqual(LocateKind.Nearest, hit.Kind);
                Assert.AreEqual(bestNearDist, hit.Distance, 1e-4f);
            }
            else if (bestInside >= 0)
            {
                Assert.AreEqual(LocateKind.Inside, hit.Kind);
                Assert.AreEqual(bestInsideDist, hit.Distance, 1e-4f);
            }
            else
            {
                Assert.AreEqual(LocateKind.None, hit.Kind);
            }
        }
    }

    // ---- polygon file -------------------------------------------------------------------

    [TestMethod]
    public void NavPolyFile_RoundTripsVerticesAndLayers()
    {
        var polys = new List<NavPolygon>
        {
            new(Quad(1.5f, -2.25f, 4, 3.125f), 0),
            new(new[] { new Vector3(-10, 5, 7), new Vector3(-8, 5.5f, 7), new Vector3(-9, 6, 9) }, 6),
        };
        using var ms = new MemoryStream();
        NavPolyFile.Write(ms, polys);
        ms.Position = 0;
        var read = NavPolyFile.Read(ms);

        Assert.AreEqual(polys.Count, read.Count);
        for (int i = 0; i < polys.Count; i++)
        {
            Assert.AreEqual(polys[i].Layer, read[i].Layer);
            CollectionAssert.AreEqual(polys[i].Vertices, read[i].Vertices);
        }
        // 12-byte header, then 2 bytes + 12 per vertex for each polygon.
        Assert.AreEqual(12 + (2 + 4 * 12) + (2 + 3 * 12), ms.Length);
    }

    [TestMethod]
    public void NavPolyFile_RejectsForeignData()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        Assert.ThrowsException<InvalidDataException>(() => NavPolyFile.Read(ms));
    }

    // ---- orientation --------------------------------------------------------------------

    private static IEnumerable<Orientation> AllOrientations()
    {
        foreach (bool fx in new[] { false, true })
        foreach (bool fz in new[] { false, true })
        for (int r = 0; r < 4; r++)
            yield return new Orientation(fx, fz, r);
    }

    [TestMethod]
    public void Transform_RoundTripsInEveryOrientation()
    {
        var rng = new Random(3);
        foreach (var o in AllOrientations())
        {
            var t = MapTransform.FitBounds(-169.3f, 261.6f, -136.0f, 508.5f, 6.0, 3.0, o.FlipX, o.FlipZ, o.Rotation);
            for (int i = 0; i < 200; i++)
            {
                double wx = -169.3 + rng.NextDouble() * 430.9, wz = -136.0 + rng.NextDouble() * 644.5;
                var (px, py) = t.ToPixel(wx, wz);
                Assert.IsTrue(px >= 0 && px <= t.Width && py >= 0 && py <= t.Height, $"pixel outside the image for {o}");
                var (rx, rz) = t.ToWorld(px, py);
                Assert.AreEqual(wx, rx, 1e-6, $"X round-trip for {o}");
                Assert.AreEqual(wz, rz, 1e-6, $"Z round-trip for {o}");
            }
        }
    }

    [TestMethod]
    public void Transform_RotationIsClockwiseQuarterTurns()
    {
        // 100 x 50 m, 1 px/m, no padding: unrotated image is 100 wide, 50 high.
        var t0 = MapTransform.FitBounds(0, 100, 0, 50, 1.0, 0.0, false, false, 0);
        var t1 = t0.WithOrientation(new Orientation(false, false, 1));
        Assert.AreEqual((50, 100), (t1.Width, t1.Height));

        // The unrotated top-left corner ends up top-right; top-right ends up bottom-right.
        Assert.AreEqual((50.0, 0.0), t1.ToPixel(0, 0));
        Assert.AreEqual((50.0, 100.0), t1.ToPixel(100, 0));
        Assert.AreEqual((0.0, 0.0), t1.ToPixel(0, 50));

        // Four quarter turns are the identity; negative turns wrap.
        Assert.AreEqual(0, new Orientation(false, false, 4).QuarterTurns);
        Assert.AreEqual(3, new Orientation(false, false, -1).QuarterTurns);
    }

    [TestMethod]
    public void Transform_BothFlipsEqualAHalfTurn()
    {
        var flipped = MapTransform.FitBounds(-20, 80, 5, 65, 4.0, 3.0, true, true, 0);
        var turned = MapTransform.FitBounds(-20, 80, 5, 65, 4.0, 3.0, false, false, 2);
        var (ax, ay) = flipped.ToPixel(13.5, 40.25);
        var (bx, by) = turned.ToPixel(13.5, 40.25);
        Assert.AreEqual(ax, bx, 1e-9);
        Assert.AreEqual(ay, by, 1e-9);
    }

    // ---- config.json --------------------------------------------------------------------

    private const string SampleConfig = """
        {
          // Game install root. "flipX": true in this comment must not be touched.
          "gamePath": "G:\\Games\\DARK SOULS III",
          "mapId": "m30_00_00_00",
          "pixelsPerMeter": 6.0,
          // Orientation of the map image.
          "flipX": false,
          "flipZ": false,
          "rotation": 0,
          "layerSplitHeight": 1.8
        }
        """;

    [TestMethod]
    public void ConfigFile_SavesOrientation_KeepingCommentsAndOtherSettings()
    {
        string updated = ConfigFile.SetOrientation(SampleConfig, new Orientation(true, false, 3));

        StringAssert.Contains(updated, "\"flipX\": true,");
        StringAssert.Contains(updated, "\"flipZ\": false,");
        StringAssert.Contains(updated, "\"rotation\": 3,");
        StringAssert.Contains(updated, "// Game install root. \"flipX\": true in this comment must not be touched.");
        StringAssert.Contains(updated, "\"gamePath\": \"G:\\\\Games\\\\DARK SOULS III\"");
        // Only the two changed values differ.
        Assert.AreEqual(SampleConfig.Replace("\"flipX\": false", "\"flipX\": true").Replace("\"rotation\": 0", "\"rotation\": 3"), updated);
    }

    [TestMethod]
    public void ConfigFile_AddsMissingKeys_AndRoundTripsThroughTheLoader()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lgm-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            // A phase 1 config: no "rotation" key yet.
            File.WriteAllText(path, SampleConfig.Replace("  \"rotation\": 0,\n", "").Replace("  \"rotation\": 0,\r\n", ""));
            Assert.IsFalse(File.ReadAllText(path).Contains("rotation"));

            foreach (var o in AllOrientations())
            {
                ConfigFile.SaveOrientation(path, o);
                var cfg = Config.Load(path);
                Assert.AreEqual(o, cfg.Orientation);
                Assert.AreEqual("m30_00_00_00", cfg.MapId);
                Assert.AreEqual(@"G:\Games\DARK SOULS III", cfg.GamePath);
                Assert.AreEqual(1.8f, cfg.LayerSplitHeight);
            }
            StringAssert.Contains(File.ReadAllText(path), "// Orientation of the map image.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- replay and tracking --------------------------------------------------------------

    [TestMethod]
    public void ReplayFile_ParsesPositionsWithOptionalTimeColumn()
    {
        var positions = ReplayFile.Parse(new[]
        {
            "# comment", "", "1.5,2,-3.25", "0.04, 4, 5, 6", "7;8;9", "10\t11\t12",
        });
        CollectionAssert.AreEqual(new[]
        {
            new Vector3(1.5f, 2, -3.25f), new Vector3(4, 5, 6), new Vector3(7, 8, 9), new Vector3(10, 11, 12),
        }, positions);
        Assert.ThrowsException<InvalidDataException>(() => ReplayFile.Parse(new[] { "1,2" }));
        Assert.ThrowsException<InvalidDataException>(() => ReplayFile.Parse(new[] { "1,two,3" }));
    }

    [TestMethod]
    public void NavPath_ResamplesToEvenSteps_AndKeepsBothEnds()
    {
        var corners = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 0, 4) };
        var path = NavPath.Resample(corners, 0.5f);
        Assert.AreEqual(corners[0], path[0]);
        Assert.AreEqual(corners[^1], path[^1]);
        Assert.AreEqual(15, path.Count);   // 7 m in 0.5 m steps, both ends included
        for (int i = 1; i < path.Count; i++)
            Assert.AreEqual(0.5f, Vector3.Distance(path[i - 1], path[i]), 0.21f);
    }

    private static MapData TwoFloorMap()
    {
        // A ground strip (layer 0) with stairs up to a gallery (layer 1) that runs back over it.
        var polys = new List<NavPolygon>
        {
            new(Quad(0, 0, 4, 0), 0), new(Quad(4, 0, 4, 0), 0), new(Quad(8, 0, 4, 0), 0),
            new(Ramp(12, 0, 0, 5), 0),
            new(Quad(16, 0, 4, 5), 0),
            new(Quad(8, 0, 4, 5), 1), new(Quad(4, 0, 4, 5), 1),
        };
        var doc = new MapDocument { Map = "test" };
        doc.SetTransform(MapTransform.FitBounds(0, 20, 0, 4, 10.0, 1.0, false, false, 0));
        return MapData.Create("(memory)", doc, polys);
    }

    [TestMethod]
    public void NavPath_WalksConnectedPolygons_BetweenTwoPoints()
    {
        var map = TwoFloorMap();
        var from = new Vector3(1, 0, 2);
        var to = new Vector3(18, 5, 2);
        var path = NavPath.Between(map, from, to, 0.5f, out bool along);
        Assert.IsTrue(along);
        Assert.AreEqual(from, path[0]);
        Assert.AreEqual(to, path[^1]);
        // Every step stays on the mesh.
        foreach (var p in path)
            Assert.IsTrue(map.Locator.Locate(p).Distance < 1.0f, $"path left the mesh at {p}");

        // Points with no mesh between them fall back to a straight line.
        var lonely = MapData.Create("(memory)", map.Document, new List<NavPolygon> { new(Quad(0, 0, 4, 0), 0), new(Quad(50, 0, 4, 0), 0) });
        NavPath.Between(lonely, new Vector3(2, 0, 2), new Vector3(52, 0, 2), 1f, out bool connected);
        Assert.IsFalse(connected);
    }

    [TestMethod]
    public void TrackingSession_FollowsLayers_AndKeepsATrail()
    {
        var map = TwoFloorMap();
        var session = new TrackingSession(map, Orientation.Default);

        var onGround = session.Update(PositionSample.At(new Vector3(6, 0, 2)));
        Assert.AreEqual(TrackState.Tracking, onGround.State);
        Assert.AreEqual(0, onGround.Layer);

        // Same top-down spot, on the gallery above: the layer follows the polygon, not the pixel.
        var onGallery = session.Update(PositionSample.At(new Vector3(6, 5, 2)));
        Assert.AreEqual(1, onGallery.Layer);
        Assert.AreEqual(onGround.PixelX, onGallery.PixelX);
        Assert.AreEqual(onGround.PixelY, onGallery.PixelY);

        session.Update(PositionSample.At(new Vector3(7, 5, 2)));
        Assert.AreEqual(3, session.Trail.Count(p => p.HasValue));

        // A step smaller than the minimum does not add a point.
        session.Update(PositionSample.At(new Vector3(7.05f, 5, 2)));
        Assert.AreEqual(3, session.Trail.Count(p => p.HasValue));
    }

    [TestMethod]
    public void TrackingSession_ReportsEveryGracefulState_AndRecovers()
    {
        var session = new TrackingSession(TwoFloorMap(), Orientation.Default);

        Assert.AreEqual(TrackState.GameNotRunning, session.Update(PositionSample.Of(SourceStatus.GameNotRunning)).State);
        Assert.AreEqual(TrackState.AccessDenied, session.Update(PositionSample.Of(SourceStatus.AccessDenied)).State);
        Assert.AreEqual(TrackState.Unsupported, session.Update(PositionSample.Of(SourceStatus.Unsupported)).State);
        Assert.AreEqual(TrackState.NoCharacter, session.Update(PositionSample.Of(SourceStatus.NoCharacter)).State);

        // A position far from this map's navmesh: outside the map, no layer, but the raw position is still shown.
        var away = session.Update(PositionSample.At(new Vector3(500, 0, 500)));
        Assert.AreEqual(TrackState.OutsideMap, away.State);
        Assert.AreEqual(-1, away.Layer);
        Assert.IsTrue(away.HasPosition);

        // Same top-down spot as the map but far below it (another area under this one).
        Assert.AreEqual(TrackState.OutsideMap, session.Update(PositionSample.At(new Vector3(6, -80, 2))).State);

        // Garbage from memory is not a position.
        Assert.AreEqual(TrackState.NoCharacter, session.Update(PositionSample.At(new Vector3(float.NaN, 0, 0))).State);

        // Back on the map: tracking again, with no stale state.
        var back = session.Update(PositionSample.At(new Vector3(6, 0, 2)));
        Assert.AreEqual(TrackState.Tracking, back.State);
        Assert.AreEqual(0, back.Layer);
    }

    [TestMethod]
    public void TrackingSession_BreaksTheTrail_OnWarpAndOnLostTracking()
    {
        var session = new TrackingSession(TwoFloorMap(), Orientation.Default);
        session.Update(PositionSample.At(new Vector3(1, 0, 2)));
        session.Update(PositionSample.At(new Vector3(2, 0, 2)));
        session.Update(PositionSample.Of(SourceStatus.NoCharacter));       // loading screen
        session.Update(PositionSample.At(new Vector3(18, 5, 2)));          // respawned elsewhere

        var trail = session.Trail.ToList();
        Assert.AreEqual(4, trail.Count);
        Assert.IsNull(trail[2], "the trail must not draw a line across a break");
        Assert.AreEqual(new Vector3(18, 5, 2), trail[3]);
    }

    [TestMethod]
    public void TrackingSession_OrientationChange_MovesDotWithTheMap()
    {
        var map = TwoFloorMap();
        var session = new TrackingSession(map, Orientation.Default);
        var world = new Vector3(6, 0, 2);
        session.Update(PositionSample.At(world));

        foreach (var o in AllOrientations())
        {
            session.SetOrientation(o);
            var expected = map.TransformFor(o).ToPixel(world);
            Assert.AreEqual(expected.X, session.Current.PixelX, 1e-9, o.ToString());
            Assert.AreEqual(expected.Y, session.Current.PixelY, 1e-9, o.ToString());
            // The trail is kept in world space, so it needs no fixing up.
            Assert.AreEqual(world, session.Trail[0]);
        }
    }
}
