using System.Numerics;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MapBuilder.Tests;

/// <summary>
/// What the packaged app needs around the tracker: finding the game through Steam's library
/// list, noticing changed archives, remembering its state, the feedback note, and what to do
/// with a map id that is not one. Made-up folders, files and names only.
/// </summary>
[TestClass]
public class AppSupportTests
{
    private static Vector3[] Quad(float x, float z, float size, float y) => new[]
    {
        new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y, z + size), new Vector3(x, y, z + size),
    };

    private static MapData Map(string id, string name, float x0, params MapDocument.MarkerInfo[] markers)
    {
        var polys = Enumerable.Range(0, 10).Select(i => new NavPolygon(Quad(x0 + i * 4, 0, 8, 0), 0)).ToList();
        var doc = new MapDocument { Map = id, Name = name, Markers = markers.ToList() };
        doc.SetTransform(MapTransform.FitBounds(x0, x0 + 44, 0, 8, 6.0, 3.0, false, false, 0));
        return MapData.Create("(memory)", doc, polys);
    }

    private static MapDocument.MarkerInfo Treasure(string name, int flag, float x, bool offMesh = false) => new()
    {
        Type = MapDocument.TreasureType, Name = name, Layer = 0, World = new[] { x, 0f, 4f }, Flag = flag, Category = ItemCategories.Key, OffMesh = offMesh,
        Items = { new MapDocument.ItemInfo { Name = name, Category = ItemCategories.Key } },
    };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lgm-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    // ---- a map id that is not a map id ---------------------------------------------------

    /// <summary>Keep covers x 0..44, Annex 40..84, Far 200..244; one more id is known to the game but has no map.</summary>
    private static (MapSet Set, MapData Keep, MapData Annex, MapData Far) KnownMaps()
    {
        var keep = Map("m70_00_00_00", "Test Keep", 0);
        var annex = Map("m70_01_00_00", "Test Annex", 40);
        var far = Map("m71_00_00_00", "Test Far Shore", 200);
        var set = MapSet.Of(keep, annex, far);
        set.Index.KnownMapIds.AddRange(new[] { keep.Id, annex.Id, far.Id, "m72_00_00_00" });
        return (set, keep, annex, far);
    }

    [TestMethod]
    public void UnknownMapId_IsNotTrusted_AndThePositionDecidesWhereItCan()
    {
        var (set, keep, annex, far) = KnownMaps();
        var session = new TrackingSession(set, keep.Id, Orientation.Default);
        const string garbage = "m12_34_56_78";   // shaped like a map id, but not one the game has

        // On the shown map: keep tracking there, and say that the id was not usable.
        var f = session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), garbage), T0);
        Assert.AreEqual(TrackState.Tracking, f.State);
        Assert.AreEqual(garbage, f.UnknownMapId);
        Assert.AreSame(keep, session.Map);
        Assert.AreEqual(TrackingSession.MethodPositionUnknownId, session.MapMethod);

        // Off the shown map and on exactly one other: switch by position, and log how it was decided.
        f = session.Update(PositionSample.AtMap(new Vector3(220, 0, 4), garbage), T0.AddSeconds(1));
        Assert.AreSame(far, session.Map);
        Assert.AreEqual(TrackState.Tracking, f.State);
        Assert.AreEqual(garbage, f.UnknownMapId);
        var switched = session.DrainEvents().Single();
        Assert.AreEqual(TrackEventKind.MapSwitch, switched.Kind);
        StringAssert.Contains(LiveCheckLog.Format(switched), "unsupported game build");

        // Nowhere near any map: nothing to switch to, the shown map stays and the player is off it.
        f = session.Update(PositionSample.AtMap(new Vector3(900, 0, 900), garbage), T0.AddSeconds(2));
        Assert.AreSame(far, session.Map);
        Assert.AreEqual(TrackState.OutsideMap, f.State);
        Assert.AreEqual(garbage, f.UnknownMapId);

        // A real id again: trusted at once, and the warning is gone.
        f = session.Update(PositionSample.AtMap(new Vector3(42, 0, 4), annex.Id), T0.AddSeconds(3));
        Assert.AreSame(annex, session.Map);
        Assert.IsNull(f.UnknownMapId);
        Assert.AreEqual(TrackingSession.MethodMapId, session.MapMethod);
    }

    [TestMethod]
    public void UnknownMapId_WhereTwoMapsShareTheSpot_DoesNotGuess()
    {
        var (set, keep, _, far) = KnownMaps();
        var session = new TrackingSession(set, far.Id, Orientation.Default);

        // x = 42 lies on both Keep and Annex: position cannot tell them apart, so the shown map stays.
        var f = session.Update(PositionSample.AtMap(new Vector3(42, 0, 4), "m12_34_56_78"), T0);
        Assert.AreSame(far, session.Map);
        Assert.AreEqual(TrackState.OutsideMap, f.State);
        Assert.AreEqual(0, session.DrainEvents().Count);

        // x = 10 lies on Keep only.
        session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), "m12_34_56_78"), T0.AddSeconds(1));
        Assert.AreSame(keep, session.Map);
    }

    [TestMethod]
    public void KnownButUnbuiltMapId_IsStillReportedAsAMapWithoutData_NotAsAnUnsupportedBuild()
    {
        var (set, keep, _, _) = KnownMaps();
        var session = new TrackingSession(set, keep.Id, Orientation.Default);
        var f = session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), "m72_00_00_00"), T0);
        Assert.AreEqual(TrackState.OutsideMap, f.State);
        Assert.IsNull(f.UnknownMapId);
        StringAssert.Contains(f.Detail, "m72_00_00_00");

        // The index decides what is known; without a list, the built maps are the known ones.
        Assert.IsTrue(set.Index.IsKnownMapId("M72_00_00_00"));
        Assert.IsFalse(set.Index.IsKnownMapId("m12_34_56_78"));
        Assert.IsFalse(set.Index.IsKnownMapId(null));
        var bare = MapSet.Of(keep);
        Assert.IsTrue(bare.Index.IsKnownMapId(keep.Id));
        Assert.IsFalse(bare.Index.IsKnownMapId("m72_00_00_00"));
    }

    // ---- finding the game ----------------------------------------------------------------

    [TestMethod]
    public void SteamLibrary_ReadsBothLayoutsOfTheLibraryList()
    {
        const string current = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"contentid"		"1234567890123456789"
            		"apps"
            		{
            			"228980"		"151515151"
            			"374320"		"26000000000"
            		}
            	}
            	"1"
            	{
            		"path"		"G:\\SteamLibrary"
            		"apps"
            		{
            			"1245620"		"50000000000"
            		}
            	}
            }
            """;
        CollectionAssert.AreEqual(new[] { @"C:\Program Files (x86)\Steam", @"G:\SteamLibrary" }, SteamLibrary.ParseLibraryFolders(current));

        const string old = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1600000000"
            	"ContentStatsID"		"-1234567890"
            	"1"		"D:\\Games\\Steam Library"
            	"2"		"E:\\More"
            }
            """;
        CollectionAssert.AreEqual(new[] { @"D:\Games\Steam Library", @"E:\More" }, SteamLibrary.ParseLibraryFolders(old));

        Assert.AreEqual(0, SteamLibrary.ParseLibraryFolders("").Count);
        Assert.AreEqual(0, SteamLibrary.ParseLibraryFolders(null).Count);
        Assert.AreEqual(0, SteamLibrary.ParseLibraryFolders("not a library list at all { \"path\" }").Count);
    }

    [TestMethod]
    public void SteamLibrary_FindsTheGameInASecondLibrary_AndOnlyWhereTheArchivesAre()
    {
        string root = TempDir();
        try
        {
            // A Steam folder whose library list names a second library; the game is only in the second.
            string steam = Path.Combine(root, "Steam"), second = Path.Combine(root, "Second Library"), third = Path.Combine(root, "Third");
            Directory.CreateDirectory(Path.Combine(steam, "config"));
            string Escaped(string p) => p.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(steam, "config", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{Escaped(steam)}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{Escaped(third)}\"\n\t}}\n\t\"2\"\n\t{{\n\t\t\"path\"\t\t\"{Escaped(second)}\"\n\t}}\n}}\n");

            // The third library has the game's folder but no archives in it (an uninstalled leftover).
            Directory.CreateDirectory(SteamLibrary.GamePathIn(third));
            Assert.IsNull(SteamLibrary.FindGame(new[] { steam }));

            string game = SteamLibrary.GamePathIn(second);
            Directory.CreateDirectory(Path.Combine(game, "Game"));
            File.WriteAllBytes(Path.Combine(game, "Game", "Data1.bhd"), new byte[] { 1, 2, 3 });

            Assert.AreEqual(game, SteamLibrary.FindGame(new[] { Path.Combine(root, "does not exist"), "", steam }));
            Assert.IsTrue(SteamLibrary.IsGameFolder(game));
            Assert.IsTrue(SteamLibrary.IsGameFolder(Path.Combine(game, "Game")));
            Assert.IsFalse(SteamLibrary.IsGameFolder(SteamLibrary.GamePathIn(third)));
            Assert.IsFalse(SteamLibrary.IsGameFolder(""));
            Assert.IsFalse(SteamLibrary.IsGameFolder(null));
            Assert.AreEqual(Path.Combine(game, "Game"), SteamLibrary.ArchiveFolder(game));
            Assert.AreEqual(Path.Combine(game, "Game"), SteamLibrary.ArchiveFolder(Path.Combine(game, "Game")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- rebuild detection ---------------------------------------------------------------

    [TestMethod]
    public void BuildStamp_ChangesWhenAnArchiveChangesOrArrives_AndOnlyThen()
    {
        string root = TempDir();
        try
        {
            string game = Path.Combine(root, "Game");
            Directory.CreateDirectory(game);
            var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            void Put(string name, int size)
            {
                File.WriteAllBytes(Path.Combine(game, name), new byte[size]);
                File.SetLastWriteTimeUtc(Path.Combine(game, name), time);
            }
            Put("Data1.bhd", 10);
            Put("Data1.bdt", 1000);
            string first = BuildStamp.Compute(root);
            Assert.AreEqual(32, first.Length);
            Assert.AreEqual(first, BuildStamp.Compute(root), "nothing changed");
            Assert.AreEqual(first, BuildStamp.Compute(game), "the game folder or its Game subfolder: the same archives");

            // A file that is not an archive does not matter (the game and its mods write logs and settings there).
            Put("notes.ini", 5);
            Assert.AreEqual(first, BuildStamp.Compute(root));

            // DLC downloaded.
            Put("DLC1.bhd", 10);
            Put("DLC1.bdt", 500);
            string withDlc = BuildStamp.Compute(root);
            Assert.AreNotEqual(first, withDlc);

            // A patch: same names, another size, or only another time.
            Put("Data1.bdt", 1001);
            string patched = BuildStamp.Compute(root);
            Assert.AreNotEqual(withDlc, patched);
            File.SetLastWriteTimeUtc(Path.Combine(game, "Data1.bdt"), time.AddHours(1));
            Assert.AreNotEqual(patched, BuildStamp.Compute(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void AppState_RoundTrips_AndAMissingOrBrokenFileMeansAFreshStart()
    {
        string root = TempDir();
        try
        {
            string path = Path.Combine(root, "nested", "app.json");
            Assert.IsNull(AppState.Load(path).GamePath);

            var state = new AppState
            {
                GamePath = @"X:\Library\steamapps\common\DARK SOULS III", FlipX = true, Rotation = 3,
                BuiltStamp = "ABCDEF", BuiltUtc = "2026-01-01T00:00:00.0000000Z", BuiltWithVersion = "0.1.0-test", BuildSeconds = 18.7, MapsBuilt = 20, DlcBuilt = true,
            };
            state.Save(path);
            var loaded = AppState.Load(path);
            Assert.AreEqual(state.GamePath, loaded.GamePath);
            Assert.IsTrue(loaded.FlipX);
            Assert.IsFalse(loaded.FlipZ);
            Assert.AreEqual(3, loaded.Rotation);
            Assert.AreEqual("ABCDEF", loaded.BuiltStamp);
            Assert.AreEqual(18.7, loaded.BuildSeconds);
            Assert.AreEqual(20, loaded.MapsBuilt);
            Assert.IsTrue(loaded.DlcBuilt);

            File.WriteAllText(path, "{ not json");
            Assert.IsNull(AppState.Load(path).GamePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- feedback ------------------------------------------------------------------------

    [TestMethod]
    public void FeedbackNote_SaysWhenWhereAndWhatIsAround()
    {
        var keep = Map("m70_00_00_00", "Test Keep", 0,
            Treasure("Near Key", 57000100, 12), Treasure("Far Key", 57000110, 40), Treasure("Ledge Key", 57000120, 14, offMesh: true),
            new MapDocument.MarkerInfo { Type = MapDocument.TreasureType, Name = "leftover", Layer = -1, World = new[] { 10f, 0f, 4f }, Unused = true });
        var session = new TrackingSession(MapSet.Of(keep), keep.Id, Orientation.Default);
        session.Update(PositionSample.AtMap(new Vector3(10, 0, 4), keep.Id), T0);
        session.ApplyFlags(new Dictionary<int, bool> { [57000100] = true, [57000120] = false }, T0);

        string note = FeedbackNote.Format(session, "0.1.0-test", new DateTime(2026, 3, 4, 5, 6, 7), nearest: 2);
        string[] lines = note.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

        Assert.AreEqual(3, lines.Length, note);
        StringAssert.StartsWith(lines[0], "2026-03-04T05:06:07 mark version=0.1.0-test map=m70_00_00_00 area=\"Test Keep\" state=Tracking layer=0");
        StringAssert.Contains(lines[0], "world=(10.00, 0.00, 4.00)");
        StringAssert.Contains(lines[0], "gameMap=m70_00_00_00");
        StringAssert.Contains(lines[0], "chosenBy=\"" + TrackingSession.MethodMapId + "\"");
        // The two nearest markers, nearest first; the unused leftover is never listed.
        StringAssert.Contains(lines[1], "2.0 m  treasure \"Near Key\" layer=0 flag=57000100 collected=True");
        StringAssert.Contains(lines[2], "4.0 m  treasure \"Ledge Key\" layer=0 flag=57000120 collected=False offMesh");
        Assert.IsFalse(note.Contains("leftover"));
        Assert.IsFalse(note.Contains("Far Key"));

        // Without a position there is still a line to find in the log.
        var idle = new TrackingSession(MapSet.Of(keep), keep.Id, Orientation.Default);
        idle.Update(PositionSample.Of(SourceStatus.GameNotRunning, "", "not running"), T0);
        string idleNote = FeedbackNote.Format(idle, "0.1.0-test", T0);
        Assert.AreEqual(1, idleNote.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        StringAssert.Contains(idleNote, "state=GameNotRunning");
    }
}
