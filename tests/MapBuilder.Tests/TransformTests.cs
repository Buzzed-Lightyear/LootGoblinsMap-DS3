using System.Numerics;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

[TestClass]
public class TransformTests
{
    [TestMethod]
    public void RoundTrip_WithinOneCentimeter()
    {
        foreach (bool flipX in new[] { false, true })
        foreach (bool flipZ in new[] { false, true })
        {
            var t = MapTransform.FitBounds(-169.3f, 261.6f, -136.0f, 508.5f, 6.0, 3.0, flipX, flipZ);
            var rng = new Random(42);
            for (int i = 0; i < 1000; i++)
            {
                double wx = -169.3 + rng.NextDouble() * (261.6 + 169.3);
                double wz = -136.0 + rng.NextDouble() * (508.5 + 136.0);
                var (px, py) = t.ToPixel(wx, wz);
                var (rx, rz) = t.ToWorld(px, py);
                Assert.IsTrue(Math.Abs(rx - wx) < 0.01, $"X round-trip off by {Math.Abs(rx - wx)} m (flip {flipX}/{flipZ})");
                Assert.IsTrue(Math.Abs(rz - wz) < 0.01, $"Z round-trip off by {Math.Abs(rz - wz)} m (flip {flipX}/{flipZ})");
            }
        }
    }

    [TestMethod]
    public void PixelOrigin_MapsToPaddedWorldMin()
    {
        var t = MapTransform.FitBounds(10, 110, -20, 80, 2.0, 5.0, false, false);
        var (wx, wz) = t.ToWorld(0, 0);
        Assert.AreEqual(5.0, wx, 1e-9);
        Assert.AreEqual(-25.0, wz, 1e-9);
    }

    [TestMethod]
    public void PlacementMatrix_YawOnly_RotatesAroundY()
    {
        // 90° yaw: +X should go to -Z under FromSoft's convention (row-vector, CreateRotationY).
        var m = Pipeline.NavGeometry.PlacementMatrix(Vector3.Zero, new Vector3(0, MathF.PI / 2, 0), Vector3.One);
        var v = Vector3.Transform(new Vector3(1, 0, 0), m);
        Assert.AreEqual(0f, v.X, 1e-5f);
        Assert.AreEqual(-1f, v.Z, 1e-5f);
    }

    [TestMethod]
    public void PlacementMatrix_TranslatesAfterRotation()
    {
        var m = Pipeline.NavGeometry.PlacementMatrix(new Vector3(10, 20, 30), new Vector3(0, MathF.PI, 0), Vector3.One);
        var v = Vector3.Transform(new Vector3(1, 0, 0), m);
        Assert.AreEqual(9f, v.X, 1e-4f);
        Assert.AreEqual(20f, v.Y, 1e-4f);
        Assert.AreEqual(30f, v.Z, 1e-4f);
    }
}
