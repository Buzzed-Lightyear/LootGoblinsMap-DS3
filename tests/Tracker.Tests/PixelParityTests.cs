using System.Numerics;
using MapBuilder.Pipeline;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orientation = MapCore.Orientation;

namespace Tracker.Tests;

/// <summary>
/// The builder and the tracker must put the same world point on the same pixel. These tests run
/// the builder's real export on a small synthetic map, load the result the way the tracker does,
/// and compare the two.
/// </summary>
[TestClass]
public class PixelParityTests
{
    private static Vector3[] Quad(float x, float z, float size, float y) => new[]
    {
        new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y, z + size), new Vector3(x, y, z + size),
    };

    private static IEnumerable<Orientation> AllOrientations()
    {
        foreach (bool fx in new[] { false, true })
        foreach (bool fz in new[] { false, true })
        for (int r = 0; r < 4; r++)
            yield return new Orientation(fx, fz, r);
    }

    private sealed class Built : IDisposable
    {
        public required string Root;
        public required Config Config;
        public required BuildResult Result;
        public string MapDir => Result.OutDir;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    /// <summary>Runs the builder's export (layers, markers, transform, map.json, polygon file) on synthetic geometry.</summary>
    private static Built Build(Orientation orientation)
    {
        string root = Path.Combine(Path.GetTempPath(), "lgm-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = new Config
        {
            MapId = "m_test",
            OutDir = "out",
            WorkDir = "work",
            PixelsPerMeter = 6.0,
            FlipX = orientation.FlipX,
            FlipZ = orientation.FlipZ,
            Rotation = orientation.Rotation,
            ProjectRoot = root,
        };

        // An L-shaped floor with a second storey over part of it; deliberately not square and off-origin.
        var faces = new List<WorldFace>();
        void Add(Vector3[] v) { var f = new WorldFace { Vertices = v }; f.ComputeBounds(); faces.Add(f); }
        for (int i = 0; i < 6; i++) Add(Quad(-37.5f + i * 8, 12.25f, 8, 0));
        for (int i = 0; i < 9; i++) Add(Quad(-37.5f, 20.25f + i * 8, 8, 0.5f * i));
        for (int i = 0; i < 3; i++) Add(Quad(-37.5f + i * 8, 12.25f, 8, 6));

        var markers = new List<Marker>
        {
            new() { Type = MapDocument.BonfireType, Name = "bonfire_a", EntityId = 1, World = new Vector3(-30.2f, 0, 15.9f) },
            new() { Type = MapDocument.BonfireType, Name = "bonfire_b", EntityId = 2, World = new Vector3(-33.3f, 4.0f, 86.1f) },
            new() { Type = MapDocument.TreasureType, Name = "treasure_up", EntityId = 3, World = new Vector3(-20.5f, 6, 17.0f), ItemLots = { 100 } },
        };

        var result = BuildPipeline.Export(cfg, faces, markers, TextWriter.Null);
        return new Built { Root = root, Config = cfg, Result = result };
    }

    [TestMethod]
    public void TrackerPixels_EqualBuilderPixels_ForMarkersAndRandomPoints()
    {
        foreach (var orientation in AllOrientations())
        {
            using var built = Build(orientation);

            // Tracker side: load the builder's output folder and take the orientation from the same config.
            var map = MapData.Load(built.MapDir);
            var tracker = map.TransformFor(built.Config.Orientation);
            var builder = built.Result.Transform;

            Assert.AreEqual(builder.Width, tracker.Width, orientation.ToString());
            Assert.AreEqual(builder.Height, tracker.Height, orientation.ToString());

            // 1. Every marker: the pixel the builder drew and stored equals what the tracker computes.
            Assert.AreEqual(3, map.Markers.Count);
            foreach (var marker in map.Markers)
            {
                var (px, py) = tracker.ToPixel(marker.WorldPosition);
                Assert.AreEqual(marker.Pixel[0], px, 1e-9, $"{marker.Name} x, {orientation}");
                Assert.AreEqual(marker.Pixel[1], py, 1e-9, $"{marker.Name} y, {orientation}");
            }

            // 2. Arbitrary world points: identical in both programs (bit for bit, same code path).
            var rng = new Random(11);
            for (int i = 0; i < 500; i++)
            {
                double wx = -45 + rng.NextDouble() * 70, wz = 5 + rng.NextDouble() * 95;
                Assert.AreEqual(builder.ToPixel(wx, wz), tracker.ToPixel(wx, wz), $"({wx}, {wz}), {orientation}");
            }
        }
    }

    [TestMethod]
    public void TrackerReorientation_MatchesWhatARebuildWouldProduce()
    {
        // The images on disk were built one way; the user toggles to another orientation in the
        // tracker and saves it. The tracker's pixels must already equal the next build's pixels.
        using var built = Build(Orientation.Default);
        var map = MapData.Load(built.MapDir);
        var b = built.Result.Bounds;

        foreach (var orientation in AllOrientations())
        {
            var cfg = new Config
            {
                PixelsPerMeter = built.Config.PixelsPerMeter,
                FlipX = orientation.FlipX, FlipZ = orientation.FlipZ, Rotation = orientation.Rotation,
            };
            var rebuilt = BuildPipeline.CreateTransform(cfg, b.MinX, b.MaxX, b.MinZ, b.MaxZ);
            var tracker = map.TransformFor(orientation);

            Assert.AreEqual((rebuilt.Width, rebuilt.Height), (tracker.Width, tracker.Height), orientation.ToString());
            var rng = new Random(5);
            for (int i = 0; i < 300; i++)
            {
                double wx = -45 + rng.NextDouble() * 70, wz = 5 + rng.NextDouble() * 95;
                var (ex, ey) = rebuilt.ToPixel(wx, wz);
                var (ax, ay) = tracker.ToPixel(wx, wz);
                Assert.AreEqual(ex, ax, 1e-9, orientation.ToString());
                Assert.AreEqual(ey, ay, 1e-9, orientation.ToString());
            }
        }
    }

    [TestMethod]
    public void TrackerSession_PutsThePlayerOnTheBuilderPixel_AndOnTheBuilderLayer()
    {
        using var built = Build(new Orientation(true, false, 1));
        var map = MapData.Load(built.MapDir);
        var session = new TrackingSession(map, built.Config.Orientation);

        // The exported polygons are the builder's faces, layer for layer.
        Assert.AreEqual(built.Result.Faces.Count, map.Polygons.Count);
        Assert.AreEqual(map.Document.NavPolygonCount, map.Polygons.Count);
        for (int i = 0; i < map.Polygons.Count; i++)
        {
            Assert.AreEqual(built.Result.Faces[i].Layer, map.Polygons[i].Layer);
            CollectionAssert.AreEqual(built.Result.Faces[i].Vertices, map.Polygons[i].Vertices);
        }

        // Stand on each marker: the dot lands on the marker's pixel and on the marker's layer.
        foreach (var m in built.Result.MarkerList)
        {
            var frame = session.Update(PositionSample.At(m.World));
            Assert.AreEqual(TrackState.Tracking, frame.State, m.Name);
            Assert.AreEqual(m.PixelX, frame.PixelX, 1e-9, m.Name);
            Assert.AreEqual(m.PixelY, frame.PixelY, 1e-9, m.Name);
            Assert.AreEqual(m.Layer, frame.Layer, m.Name);
        }

        // Two storeys: ground under the upper floor is layer 0, the upper floor is layer 1.
        Assert.AreEqual(0, session.Update(PositionSample.At(new Vector3(-30, 0.1f, 16))).Layer);
        Assert.AreEqual(1, session.Update(PositionSample.At(new Vector3(-30, 6.1f, 16))).Layer);
    }
}
