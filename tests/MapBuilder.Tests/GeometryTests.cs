using System.Numerics;
using MapBuilder.Pipeline;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

[TestClass]
public class GeometryTests
{
    private static Vector3[] Quad(float x, float z, float size, float y = 0) => new[]
    {
        new Vector3(x, y, z),
        new Vector3(x + size, y, z),
        new Vector3(x + size, y, z + size),
        new Vector3(x, y, z + size),
    };

    [TestMethod]
    public void ConvexOverlap_DetectsOverlapAndSeparation()
    {
        Assert.IsTrue(Geometry2D.ConvexOverlapXZ(Quad(0, 0, 2), Quad(1, 1, 2)));
        Assert.IsFalse(Geometry2D.ConvexOverlapXZ(Quad(0, 0, 2), Quad(5, 5, 2)));
        // Shared edge only: not an overlap.
        Assert.IsFalse(Geometry2D.ConvexOverlapXZ(Quad(0, 0, 2), Quad(2, 0, 2)));
    }

    [TestMethod]
    public void PointToPolyDistance_InsideIsZero_OutsideIsEdgeDistance()
    {
        var q = Quad(0, 0, 4);
        Assert.AreEqual(0f, Geometry2D.DistancePointToPolyXZ(new Vector3(2, 0, 2), q));
        Assert.AreEqual(3f, Geometry2D.DistancePointToPolyXZ(new Vector3(7, 0, 2), q), 1e-5f);
        Assert.AreEqual(MathF.Sqrt(2), Geometry2D.DistancePointToPolyXZ(new Vector3(-1, 0, -1), q), 1e-5f);
    }

    [TestMethod]
    public void LayerSplitter_SeparatesStackedFloors_KeepsRampsTogether()
    {
        var faces = new List<WorldFace>();
        // Ground floor: two adjacent quads at y=0.
        faces.Add(MakeFace(Quad(0, 0, 4, 0)));
        faces.Add(MakeFace(Quad(4, 0, 4, 0)));
        // A floor 4 m above the first quad -> must split.
        faces.Add(MakeFace(Quad(0, 0, 4, 4)));
        // A gentle ramp 1 m above the second quad -> within split height, same layer OK.
        faces.Add(MakeFace(Quad(4, 0, 4, 1)));

        var layers = LayerSplitter.Split(faces, 1.8f);
        Assert.AreEqual(2, layers.Count);

        // Invariant: within a layer, no two faces overlap in XZ with a vertical gap above the threshold.
        foreach (var layer in layers)
            for (int i = 0; i < layer.Faces.Count; i++)
                for (int j = i + 1; j < layer.Faces.Count; j++)
                {
                    var a = layer.Faces[i];
                    var b = layer.Faces[j];
                    bool stacked = Geometry2D.ConvexOverlapXZ(a.Vertices, b.Vertices) &&
                                   (a.MinY > b.MaxY + 1.8f || b.MinY > a.MaxY + 1.8f);
                    Assert.IsFalse(stacked, "layer contains vertically stacked overlapping faces");
                }
    }

    private static WorldFace MakeFace(Vector3[] verts)
    {
        var f = new WorldFace { Vertices = verts };
        f.ComputeBounds();
        return f;
    }
}
