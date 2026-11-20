using System.Numerics;
using SoulsFormats;

namespace MapBuilder.Pipeline;

/// <summary>A navmesh polygon in world (map) space. Vertices are convex, counter-ordered as stored.</summary>
public sealed class WorldFace
{
    public Vector3[] Vertices;
    public int ModelId;
    public float MinY, MaxY, CenterY;
    public float MinX, MaxX, MinZ, MaxZ;
    public int Layer = -1;

    /// <summary>Part of a stray piece far from the main body; kept in the data, left out of the image bounds.</summary>
    public bool Stray;

    public void ComputeBounds()
    {
        MinY = float.MaxValue; MaxY = float.MinValue;
        MinX = float.MaxValue; MaxX = float.MinValue;
        MinZ = float.MaxValue; MaxZ = float.MinValue;
        foreach (var v in Vertices)
        {
            MinY = Math.Min(MinY, v.Y); MaxY = Math.Max(MaxY, v.Y);
            MinX = Math.Min(MinX, v.X); MaxX = Math.Max(MaxX, v.X);
            MinZ = Math.Min(MinZ, v.Z); MaxZ = Math.Max(MaxZ, v.Z);
        }
        CenterY = Vertices.Average(v => v.Y);
    }
}

public static class NavGeometry
{
    /// <summary>
    /// Builds the FromSoft placement matrix: scale, then rotation X·Z·Y (row-vector order,
    /// radians — NVA stores radians), then translation. Matches DSMapStudio/Smithbox
    /// (Transform.EulerToQuaternion: qy*qz*qx).
    /// </summary>
    public static Matrix4x4 PlacementMatrix(Vector3 position, Vector3 rotationRadians, Vector3 scale)
    {
        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateRotationX(rotationRadians.X)
             * Matrix4x4.CreateRotationZ(rotationRadians.Z)
             * Matrix4x4.CreateRotationY(rotationRadians.Y)
             * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>Decodes every placed navmesh instance of the map into world-space polygons.</summary>
    public static List<WorldFace> Load(string nvaPath, string nvmhktbndPath, TextWriter log)
    {
        var nva = NVA.Read(nvaPath);
        var bnd = BND4.Read(nvmhktbndPath);

        // Model id -> HKX bytes, matched on the "n######" stem of the binder entry name.
        var modelFiles = new Dictionary<int, BinderFile>();
        foreach (var file in bnd.Files)
        {
            // Entries are named like "...\n30_00_00_00_000100.hkx": model id is the last "_"-separated token.
            string stem = Path.GetFileNameWithoutExtension((file.Name ?? "").Replace('\\', '/'));
            int us = stem.LastIndexOf('_');
            if (us >= 0 && int.TryParse(stem.AsSpan(us + 1), out int id))
                modelFiles[id] = file;
        }
        log.WriteLine($"NVA: {nva.Navmeshes.Count} placed navmeshes; binder: {modelFiles.Count} havok navmesh models.");

        var meshCache = new Dictionary<int, List<Vector3[]>>();
        var faces = new List<WorldFace>();
        int missingModels = 0;

        foreach (var nav in nva.Navmeshes)
        {
            if (!modelFiles.TryGetValue(nav.ModelID, out var file))
            {
                missingModels++;
                log.WriteLine($"  WARNING: model n{nav.ModelID:D6} referenced by NVA entry {nav.NameID} not in binder.");
                continue;
            }

            if (!meshCache.TryGetValue(nav.ModelID, out var localPolys))
            {
                localPolys = DecodeHkxNavmeshPolys(file.Bytes, $"n{nav.ModelID:D6}", log);
                meshCache[nav.ModelID] = localPolys;
            }

            var m = PlacementMatrix(nav.Position, nav.Rotation, nav.Scale);
            foreach (var poly in localPolys)
            {
                var world = new Vector3[poly.Length];
                for (int i = 0; i < poly.Length; i++)
                    world[i] = Vector3.Transform(poly[i], m);
                var wf = new WorldFace { Vertices = world, ModelId = nav.ModelID };
                wf.ComputeBounds();
                faces.Add(wf);
            }
        }

        if (missingModels > 0)
            log.WriteLine($"  {missingModels} NVA entries had no model in the binder.");
        return faces;
    }

    /// <summary>Extracts the hkaiNavMesh polygons (local space) from a DS3 Havok 2014 packfile.</summary>
    public static List<Vector3[]> DecodeHkxNavmeshPolys(Memory<byte> hkxBytes, string label, TextWriter log)
    {
        var polys = new List<Vector3[]>();
        HKX hkx = HKX.Read(hkxBytes, HKX.HKXVariation.HKXDS3, deserializeObjects: true);
        foreach (var obj in hkx.DataSection.Objects)
        {
            if (obj is not HKX.HKAINavMesh nav)
                continue;
            int faceCount = (int)nav.Faces.Size;
            for (int f = 0; f < faceCount; f++)
            {
                var face = nav.Faces[f];
                int n = face.NumEdges;
                if (n < 3)
                    continue;
                var poly = new Vector3[n];
                for (int e = 0; e < n; e++)
                {
                    var edge = nav.Edges[face.StartEdgeIndex + e];
                    var v = nav.Vertices[edge.A].Vector;
                    poly[e] = new Vector3(v.X, v.Y, v.Z);
                }
                polys.Add(poly);
            }
        }
        if (polys.Count == 0)
            log.WriteLine($"  WARNING: {label}: no hkaiNavMesh polygons decoded.");
        return polys;
    }
}
