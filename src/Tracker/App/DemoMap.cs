using System.IO;
using System.Numerics;
using MapBuilder.Pipeline;
using MapCore;

namespace Tracker.App;

/// <summary>
/// A made-up pair of maps, built through the builder's real export, for trying the app without
/// the game: the release smoke test, the review renders and the pictures in the documentation.
/// Every shape, name and number here is invented; nothing comes from the game.
/// </summary>
internal static class DemoMap
{
    public const string KeepId = "m90_00_00_00", CellarsId = "m90_01_00_00";
    public const string WalkScript = "demo_walk.txt", ClimbScript = "demo_climb.txt", DescendScript = "demo_descend.txt";

    /// <summary>Writes the two maps and index.json into <paramref name="env"/>'s maps folder and three replay scripts into its data folder.</summary>
    public static string Write(AppEnvironment env)
    {
        Directory.CreateDirectory(env.MapsDir);
        var log = new StringWriter();
        var index = new MapIndex
        {
            Language = "demo", BuiltUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            MapsOverlapInSpace = false, KnownMapIds = { KeepId, CellarsId }, Archives = { "synthetic" },
        };

        foreach (var (id, name, faces, markers) in new[] { Keep(), Cellars() })
        {
            var cfg = new Config { MapId = id, OutDir = env.MapsDir, WorkDir = env.CacheDir, WriteLayerImages = false, ProjectRoot = env.DataDir };
            var result = BuildPipeline.Export(cfg, faces, markers, log);

            // The export takes names from the game's text; a made-up map gets its made-up name here.
            string docPath = Path.Combine(result.OutDir, MapDocument.FileName);
            var doc = MapDocument.Load(docPath);
            doc.Name = name;
            doc.Source = "synthetic";
            doc.Save(docPath);

            var b = result.Bounds;
            index.Maps.Add(new MapIndex.Entry
            {
                Id = id, Name = name, Directory = id, Source = "synthetic",
                Bounds = new[] { b.MinX, b.MaxX, b.MinY, b.MaxY, b.MinZ, b.MaxZ },
                WidthPx = result.Transform.Width, HeightPx = result.Transform.Height,
                Layers = result.Layers.Count, Polygons = faces.Count,
                Bonfires = markers.Count(m => m.Type == MapDocument.BonfireType),
                Treasures = markers.Count(m => m.Type == MapDocument.TreasureType),
                Built = true, ChecksPassed = true,
            });
        }
        index.Save(Path.Combine(env.MapsDir, MapIndex.FileName));

        // Three walks: across both maps, up to the terrace, and back down under the gallery.
        var maps = MapSet.Load(env.MapsDir, KeepId);
        var notes = new StringWriter();
        notes.Write(Headless.MakePath(maps, KeepId, CellarsId, Path.Combine(env.DataDir, WalkScript), 1, 11, 0.5f));
        notes.Write(Headless.MakePath(maps, KeepId, null, Path.Combine(env.DataDir, ClimbScript), 1, 2, 0.5f));
        notes.Write(Headless.MakePath(maps, KeepId, null, Path.Combine(env.DataDir, DescendScript), 2, 3, 0.5f));
        return $"demo maps written to {env.MapsDir}\n{notes}";
    }

    // ---- geometry ------------------------------------------------------------------------

    private sealed class Shape
    {
        public readonly List<WorldFace> Faces = new();

        /// <summary>A level floor from (x0, z0) to (x1, z1) at height y, in squares of <paramref name="cell"/> metres.</summary>
        public void Floor(float x0, float z0, float x1, float z1, float y, float cell = 4)
        {
            for (float x = x0; x < x1 - 0.01f; x += cell)
                for (float z = z0; z < z1 - 0.01f; z += cell)
                    Quad(new Vector3(x, y, z), new Vector3(Math.Min(x + cell, x1), y, z),
                         new Vector3(Math.Min(x + cell, x1), y, Math.Min(z + cell, z1)), new Vector3(x, y, Math.Min(z + cell, z1)));
        }

        /// <summary>A flight of stairs along x from height y0 at x0 to height y1 at x1, one strip per metre.</summary>
        public void StairsAlongX(float x0, float x1, float z0, float z1, float y0, float y1)
        {
            int steps = (int)MathF.Round(MathF.Abs(x1 - x0));
            for (int i = 0; i < steps; i++)
            {
                float xa = x0 + (x1 - x0) * i / steps, xb = x0 + (x1 - x0) * (i + 1) / steps;
                float ya = y0 + (y1 - y0) * i / steps, yb = y0 + (y1 - y0) * (i + 1) / steps;
                Quad(new Vector3(xa, ya, z0), new Vector3(xb, yb, z0), new Vector3(xb, yb, z1), new Vector3(xa, ya, z1));
            }
        }

        /// <summary>The same along z.</summary>
        public void StairsAlongZ(float z0, float z1, float x0, float x1, float y0, float y1)
        {
            int steps = (int)MathF.Round(MathF.Abs(z1 - z0));
            for (int i = 0; i < steps; i++)
            {
                float za = z0 + (z1 - z0) * i / steps, zb = z0 + (z1 - z0) * (i + 1) / steps;
                float ya = y0 + (y1 - y0) * i / steps, yb = y0 + (y1 - y0) * (i + 1) / steps;
                Quad(new Vector3(x0, ya, za), new Vector3(x1, ya, za), new Vector3(x1, yb, zb), new Vector3(x0, yb, zb));
            }
        }

        /// <summary>A round floor, as a fan of triangles cut into rings so no polygon is large.</summary>
        public void Disc(float cx, float cz, float radius, float y, int segments = 16)
        {
            for (int i = 0; i < segments; i++)
            {
                Add(new[] { new Vector3(cx, y, cz), Rim(cx, cz, radius / 2, y, i, segments), Rim(cx, cz, radius / 2, y, i + 1, segments) });
                Quad(Rim(cx, cz, radius / 2, y, i, segments), Rim(cx, cz, radius, y, i, segments),
                     Rim(cx, cz, radius, y, i + 1, segments), Rim(cx, cz, radius / 2, y, i + 1, segments));
            }
        }

        /// <summary>Corner <paramref name="i"/> of a disc's rim. The rim is turned so that the side from corner 0 to corner 1 faces -z.</summary>
        public static Vector3 Rim(float cx, float cz, float radius, float y, int i, int segments = 16)
        {
            float a = MathF.Tau * ((i % segments) - 0.5f) / segments - MathF.PI / 2;
            return new Vector3(cx + MathF.Cos(a) * radius, y, cz + MathF.Sin(a) * radius);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => Add(new[] { a, b, c, d });

        private void Add(Vector3[] vertices)
        {
            var face = new WorldFace { Vertices = vertices };
            face.ComputeBounds();
            Faces.Add(face);
        }
    }

    private static Marker Bonfire(int entity, string name, float x, float y, float z) => new()
    {
        Type = MapDocument.BonfireType, Name = "demo_bonfire_" + entity, EntityId = entity, World = new Vector3(x, y, z),
        DisplayName = name, Category = MapDocument.BonfireType, Flag = 90000000 + entity, Resolved = true,
    };

    private static Marker Item(int entity, string category, string name, float x, float y, float z, int count = 1) => new()
    {
        Type = MapDocument.TreasureType, Name = "demo_treasure_" + entity, EntityId = entity, World = new Vector3(x, y, z),
        ItemLots = { 900000 + entity }, DisplayName = name, Category = category, Flag = 90010000 + entity, Resolved = true,
        Items = { new MapDocument.ItemInfo { Name = name, Category = category, Count = count, ItemId = 900000 + entity } },
    };

    /// <summary>
    /// A keep: a courtyard, stairs up to a terrace, a gallery reached over a bridge that crosses
    /// the courtyard (so two storeys overlap), store rooms under the terrace, and a round vault
    /// down a second flight of stairs.
    /// </summary>
    private static (string Id, string Name, List<WorldFace> Faces, List<Marker> Markers) Keep()
    {
        var s = new Shape();
        s.Floor(0, 0, 48, 32, 0);                    // courtyard
        s.Floor(48, 0, 60, 8, 0);                    // passage to the store rooms
        s.Floor(60, 0, 84, 12, 0);                   // store rooms, under the terrace
        s.StairsAlongX(48, 60, 14, 20, 0, 6);        // main stairs
        s.Floor(60, 0, 84, 32, 6);                   // terrace
        s.Floor(40, 24, 60, 28, 6);                  // bridge over the courtyard
        s.Floor(12, 16, 40, 32, 6);                  // gallery
        s.StairsAlongZ(32, 42, 4, 10, 0, -5);        // stairs down
        // The landing narrows to the straight side of the round vault it opens into.
        s.Quad(new Vector3(4, -5, 42), new Vector3(10, -5, 42), Shape.Rim(7, 56, 10, -5, 1), Shape.Rim(7, 56, 10, -5, 0));
        s.Disc(7, 56, 10, -5);                       // round vault
        s.Floor(84, 12, 88, 20, 6);                  // gate towards the cellars

        var markers = new List<Marker>
        {
            Bonfire(1, "Goblin Courtyard", 6, 0, 6),
            Bonfire(2, "Upper Terrace", 72, 6, 22),
            Bonfire(3, "Round Vault", 7, -5, 56),
            Item(101, ItemCategories.Key, "Gatehouse Key", 30, 6, 28),
            Item(102, ItemCategories.Ring, "Ring of the Patient Goblin", 78, 0, 4),
            Item(103, ItemCategories.Weapon, "Notched Cleaver", 42, 0, 4),
            Item(104, ItemCategories.Shield, "Pot Lid Shield", 20, 0, 12),
            Item(105, ItemCategories.Catalyst, "Crooked Wand", 16, 6, 20),
            Item(106, ItemCategories.Armor, "Patchwork Hood", 66, 0, 9),
            Item(107, ItemCategories.Spell, "Scroll of Sparks", 2, -5, 62),
            Item(108, ItemCategories.Upgrade, "Whetstone Chip", 80, 6, 28, 2),
            Item(109, ItemCategories.Soul, "Glimmer of a Lost Goblin", 12, -5, 52),
            Item(110, ItemCategories.Ammo, "Bent Arrows", 54, 6, 26, 12),
            Item(111, ItemCategories.Consumable, "Moss Poultice", 36, 0, 26, 3),
            Item(112, ItemCategories.Consumable, "Lantern Oil", 64, 6, 4),
            Item(113, ItemCategories.Upgrade, "Whetstone Chip", 26, 0, 22),
            Item(114, ItemCategories.Soul, "Glimmer of a Lost Goblin", 46, 0, 30),
            // Stands in the open air next to the terrace: no floor in reach, placed by height.
            Item(115, ItemCategories.Consumable, "Curious Trinket", 96, 6.5f, 2),
        };
        return (KeepId, "Goblin Keep", s.Faces, markers);
    }

    /// <summary>The neighbouring map: a corridor from the keep's gate into a hall with a raised dais and a sunken side room.</summary>
    private static (string Id, string Name, List<WorldFace> Faces, List<Marker> Markers) Cellars()
    {
        var s = new Shape();
        s.Floor(84, 12, 100, 20, 6);                 // corridor; its first metres lie on the keep's gate
        s.Floor(100, 0, 132, 32, 6);                 // hall
        s.StairsAlongX(132, 138, 12, 20, 6, 9);      // steps up
        s.Floor(138, 8, 150, 24, 9);                 // dais
        s.StairsAlongZ(32, 40, 104, 110, 6, 2);      // steps down
        s.Floor(100, 40, 120, 52, 2);                // side room

        var markers = new List<Marker>
        {
            Bonfire(11, "Mossy Hall", 116, 6, 16),
            Item(201, ItemCategories.Weapon, "Rusted Halberd", 146, 9, 16),
            Item(202, ItemCategories.Armor, "Moth-eaten Cloak", 104, 6, 4),
            Item(203, ItemCategories.Ring, "Band of Damp Stone", 116, 2, 48),
            Item(204, ItemCategories.Consumable, "Moss Poultice", 128, 6, 28, 2),
            Item(205, ItemCategories.Key, "Cellar Key", 102, 2, 42),
            Item(206, ItemCategories.Soul, "Glimmer of a Lost Goblin", 92, 6, 14),
        };
        return (CellarsId, "Mossy Cellars", s.Faces, markers);
    }
}
