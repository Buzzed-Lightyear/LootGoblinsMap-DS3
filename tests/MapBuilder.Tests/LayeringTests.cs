using System.Numerics;
using MapBuilder.Pipeline;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

/// <summary>
/// The "connected floors" layering: one storey stays on one layer even where something lies
/// underneath part of it, small steps do not change the layer, and the change happens on stairs.
/// </summary>
[TestClass]
public class LayeringTests
{
    private const float SplitHeight = 1.8f;
    private const float JoinStep = 0.6f;

    private static WorldFace Face(params Vector3[] vertices)
    {
        var f = new WorldFace { Vertices = vertices };
        f.ComputeBounds();
        return f;
    }

    private static WorldFace Quad(float x, float z, float size, float y) => Face(
        new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y, z + size), new Vector3(x, y, z + size));

    /// <summary>A 4 m square whose height runs from <paramref name="yAtLowX"/> to <paramref name="yAtHighX"/> along X.</summary>
    private static WorldFace Ramp(float x, float z, float yAtLowX, float yAtHighX) => Face(
        new Vector3(x, yAtLowX, z), new Vector3(x + 4, yAtHighX, z), new Vector3(x + 4, yAtHighX, z + 4), new Vector3(x, yAtLowX, z + 4));

    private static FloorLayering.Result Floors(List<WorldFace> faces) =>
        FloorLayering.Split(faces, new FloorLayering.Options { SplitHeight = SplitHeight, JoinStep = JoinStep });

    private static void Stack(List<WorldFace> faces) => LayerSplitter.Split(faces, SplitHeight);

    [TestMethod]
    public void WalkwayPartlyOverARoom_StaysOnOneLayer()
    {
        // A room at ground level, and a walkway 5 m up that crosses over it and carries on beyond.
        var room = Enumerable.Range(0, 3).Select(i => Quad(i * 4, 0, 4, 0)).ToList();
        var walkway = Enumerable.Range(0, 6).Select(i => Quad(i * 4, 0, 4, 5)).ToList();
        var faces = room.Concat(walkway).ToList();

        // The old splitter decides per polygon: the walkway is split where the room ends.
        Stack(faces);
        Assert.AreEqual(2, walkway.Select(f => f.Layer).Distinct().Count(), "stack order cuts the walkway in two");
        Assert.IsTrue(FloorLayering.CountSeams(faces, JoinStep) > 0);

        // Connected floors: the whole walkway is one layer, the room another.
        var result = Floors(faces);
        Assert.AreEqual(1, walkway.Select(f => f.Layer).Distinct().Count(), "the walkway must be a single layer");
        Assert.AreEqual(1, room.Select(f => f.Layer).Distinct().Count());
        Assert.AreNotEqual(room[0].Layer, walkway[0].Layer);
        Assert.IsTrue(room[0].Layer < walkway[0].Layer, "layers are numbered bottom-up");
        Assert.AreEqual(2, result.Layers.Count);
        Assert.AreEqual(0, result.Seams);
        Assert.AreEqual(0, FloorLayering.CountSeams(faces, JoinStep));
        Assert.AreEqual(0, FloorLayering.CountSelfOverlaps(faces, SplitHeight));
    }

    [TestMethod]
    public void TinyStepBetweenSeparatePieces_DoesNotChangeTheLayer()
    {
        // Two navmesh pieces that share no corners: a 10 cm gap and a 20 cm step between them.
        // Only the first has a room underneath.
        var room = Quad(0, 0, 4, 0);
        var first = Quad(0, 0, 4, 5);
        var second = Quad(4.1f, 0, 4, 5.2f);
        var third = Quad(8.2f, 0.05f, 4, 5.5f);
        var faces = new List<WorldFace> { room, first, second, third };

        Stack(faces);
        Assert.AreNotEqual(first.Layer, second.Layer, "stack order changes layer over the tiny step");

        Floors(faces);
        Assert.AreEqual(first.Layer, second.Layer);
        Assert.AreEqual(second.Layer, third.Layer);
        Assert.AreNotEqual(room.Layer, first.Layer);
        Assert.AreEqual(0, FloorLayering.CountSelfOverlaps(faces, SplitHeight));
    }

    [TestMethod]
    public void StepLargerThanTheJoinStep_IsNotForcedOntoOneLayer_ButMayShareIt()
    {
        // A 1 m ledge is more than a "tiny step": the two sides are separate floors. With nothing
        // under either of them they still end up on the same layer, because nothing forces a split.
        var low = Quad(0, 0, 4, 0);
        var high = Quad(4, 0, 4, 1.0f);
        var faces = new List<WorldFace> { low, high };
        var result = Floors(faces);
        Assert.AreEqual(2, result.FlatPatches);
        Assert.AreEqual(low.Layer, high.Layer);
        Assert.AreEqual(1, result.Layers.Count);
    }

    [TestMethod]
    public void StairsBackOverTheirOwnFloor_ChangeLayerOnTheStairs_NotOnTheFloors()
    {
        // Ground floor along z 0..4 (x 0..16) with a landing at x 16..20 that is 8 m deep.
        // Stairs run back in the next lane (z 4..8) from the landing up to y = 6.
        // The upper floor (y = 6) covers x 0..8 in both lanes, so half of it is over the ground floor.
        var ground = Enumerable.Range(0, 4).Select(i => Quad(i * 4, 0, 4, 0)).ToList();
        ground.Add(Quad(16, 0, 4, 0));
        ground.Add(Quad(16, 4, 4, 0));
        var stairs = new List<WorldFace> { Ramp(12, 4, 3, 0), Ramp(8, 4, 6, 3) };
        var upper = new List<WorldFace> { Quad(0, 4, 4, 6), Quad(4, 4, 4, 6), Quad(0, 0, 4, 6), Quad(4, 0, 4, 6) };
        var faces = ground.Concat(stairs).Concat(upper).ToList();

        var result = Floors(faces);

        Assert.AreEqual(1, ground.Select(f => f.Layer).Distinct().Count(), "the ground floor is one layer");
        Assert.AreEqual(1, upper.Select(f => f.Layer).Distinct().Count(), "the upper floor is one layer");
        Assert.IsTrue(ground[0].Layer < upper[0].Layer);
        Assert.AreEqual(0, result.SeamsLevelToLevel, "no layer change between two level polygons");
        // Each stair polygon belongs to one of the two floors it joins.
        foreach (var s in stairs)
            Assert.IsTrue(s.Layer == ground[0].Layer || s.Layer == upper[0].Layer);
        // Exactly one cut along the flight.
        int changes = 0;
        var route = new[] { ground[5], stairs[0], stairs[1], upper[1] };
        for (int i = 1; i < route.Length; i++)
            if (route[i].Layer != route[i - 1].Layer)
                changes++;
        Assert.AreEqual(1, changes);
        Assert.AreEqual(0, FloorLayering.CountSelfOverlaps(faces, SplitHeight));
    }

    [TestMethod]
    public void SlopedPolygonInsideAFloor_GoesWithThatFloor()
    {
        // A lower room, and above it a floor with one tilted polygon in the middle (rubble, a kerb).
        // The tilted one counts as "steep" on its own, but it must not light up as another layer.
        var room = Enumerable.Range(0, 3).Select(i => Quad(i * 4, 0, 4, 0)).ToList();
        var floorLeft = Quad(0, 0, 4, 5);
        var tilted = Ramp(4, 0, 5, 6.5f);     // 20.6° — steeper than the 15° "level" limit
        var floorRight = Quad(8, 0, 4, 6.5f);
        var faces = room.Concat(new[] { floorLeft, tilted, floorRight }).ToList();

        var result = Floors(faces);
        Assert.AreEqual(floorLeft.Layer, tilted.Layer);
        Assert.AreEqual(tilted.Layer, floorRight.Layer);
        Assert.AreNotEqual(room[0].Layer, floorLeft.Layer);
        Assert.AreEqual(0, result.Seams);
    }

    [TestMethod]
    public void AnyMesh_NoLayerLiesOverItself_AndEveryFaceGetsALayer()
    {
        var rng = new Random(21);
        for (int round = 0; round < 5; round++)
        {
            var faces = new List<WorldFace>();
            // Several storeys of touching tiles with holes, plus scattered loose pieces and ramps.
            for (int storey = 0; storey < 4; storey++)
                for (int x = 0; x < 10; x++)
                    for (int z = 0; z < 10; z++)
                        if (rng.NextDouble() < 0.6)
                            faces.Add(Quad(x * 4, z * 4, 4, storey * 3.5f + (float)rng.NextDouble() * 0.3f));
            for (int i = 0; i < 40; i++)
            {
                float x = (float)rng.NextDouble() * 36, z = (float)rng.NextDouble() * 36, y = (float)rng.NextDouble() * 12;
                faces.Add(rng.Next(2) == 0 ? Quad(x, z, 4, y) : Ramp(x, z, y, y + 3));
            }

            var result = Floors(faces);

            Assert.AreEqual(0, FloorLayering.CountSelfOverlaps(faces, SplitHeight), "a layer lies over itself");
            Assert.IsTrue(faces.All(f => f.Layer >= 0 && f.Layer < result.Layers.Count));
            Assert.AreEqual(faces.Count, result.Layers.Sum(l => l.Faces.Count));
            for (int i = 0; i < result.Layers.Count; i++)
            {
                Assert.AreEqual(i, result.Layers[i].Index);
                Assert.IsTrue(result.Layers[i].Faces.Count > 0, "no empty layers");
                Assert.IsTrue(result.Layers[i].Faces.All(f => f.Layer == i));
            }
            Assert.AreEqual(result.Seams, FloorLayering.CountSeams(faces, JoinStep), "the reported seam count matches an independent count");

            // On these fixed meshes it is also no worse than stack order on the thing it is meant to
            // fix. (Not a guarantee of the method, which is a heuristic; a regression check.)
            int floorsSeams = result.Seams;
            Stack(faces);
            Assert.IsTrue(floorsSeams <= FloorLayering.CountSeams(faces, JoinStep),
                $"connected floors left {floorsSeams} seams, stack order {FloorLayering.CountSeams(faces, JoinStep)}");
        }
    }
}
