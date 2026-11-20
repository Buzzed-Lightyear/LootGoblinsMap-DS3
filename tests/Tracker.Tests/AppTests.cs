using System.IO.Compression;
using System.Numerics;
using System.Windows.Media.Imaging;
using MapCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tracker.App;
using Tracker.Rendering;
using Tracker.Sources;

namespace Tracker.Tests;

/// <summary>
/// The packaged app without the game: the themes, the made-up demo maps built through the real
/// export, a replay rendered to a picture in each theme (what the release smoke test does),
/// the feedback note and the feedback zip.
/// </summary>
[TestClass]
public class AppTests
{
    public TestContext TestContext { get; set; } = null!;

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lgm-apptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>WPF drawing wants a single-threaded apartment; the test runner's threads are not.</summary>
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new AssertFailedException(failure.ToString());
    }

    // ---- themes --------------------------------------------------------------------------

    [TestMethod]
    public void Theme_BuiltInsRoundTripThroughJson_AndCoverEveryCategory()
    {
        var names = Theme.BuiltIn().Select(t => t.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "parchment", "plain" }, names);
        foreach (var theme in Theme.BuiltIn())
        {
            var back = Theme.FromJson(theme.ToJson());
            Assert.AreEqual(theme.ToJson(), back.ToJson(), theme.Name);
            foreach (string category in ItemCategories.All)
                Assert.IsTrue(theme.CategoryColors.ContainsKey(category), $"{theme.Name} has no colour for {category}");
            // Every colour in a built-in theme is readable (an unreadable one would come out magenta).
            foreach (var property in typeof(Theme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name is not ("Name" or "LabelFont" or "TitleFont")))
            {
                string value = (string)property.GetValue(theme)!;
                if (value.Length > 0)
                    Assert.AreNotEqual(System.Windows.Media.Colors.Magenta, Theme.ParseColor(value), $"{theme.Name}.{property.Name} = '{value}'");
            }
        }
        Assert.AreEqual(System.Windows.Media.Colors.Magenta, Theme.ParseColor("not a colour"));
        Assert.AreEqual(System.Windows.Media.Color.FromArgb(0x80, 0x10, 0x20, 0x30), Theme.ParseColor("#80102030"));
    }

    [TestMethod]
    public void Theme_FilesInTheThemesFolderWin_AndABrokenOneFallsBack()
    {
        string dir = TempDir();
        try
        {
            // First use writes the built-in themes out, so there is something to edit.
            var parchment = Theme.Load(dir, "parchment");
            Assert.AreEqual("parchment", parchment.Name);
            Assert.IsTrue(File.Exists(Path.Combine(dir, "parchment.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "plain.json")));

            // An edited file is what gets loaded; a file from an older version gets the newer categories filled in.
            File.WriteAllText(Path.Combine(dir, "parchment.json"), """{ "floor": "#112233", "outlineWidth": "3.5", "categoryColors": { "key": "#FFFFFF" } }""");
            var edited = Theme.Load(dir, "parchment");
            Assert.AreEqual("#112233", edited.Floor);
            Assert.AreEqual(3.5, edited.OutlineWidth);
            Assert.AreEqual("#FFFFFF", edited.CategoryColors["key"]);
            Assert.IsTrue(ItemCategories.All.All(c => edited.CategoryColors.ContainsKey(c)));

            // A theme of the user's own.
            File.WriteAllText(Path.Combine(dir, "mine.json"), Theme.Plain().ToJson());
            CollectionAssert.AreEquivalent(new[] { "parchment", "plain", "mine" }, Theme.Available(dir));
            Assert.AreEqual("mine", Theme.Load(dir, "mine").Name);

            // Broken or missing: the built-in of that name, else plain.
            File.WriteAllText(Path.Combine(dir, "parchment.json"), "{ broken");
            Assert.AreEqual(Theme.Parchment().Floor, Theme.Load(dir, "parchment").Floor);
            Assert.AreEqual("plain", Theme.Load(dir, "no-such-theme").Name);
            Assert.AreEqual("parchment", Theme.Load(null, null).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void BundledFont_IsShippedNextToTheProgram_WithItsLicense()
    {
        string fonts = MapRenderer.FontsFolder;
        Assert.IsTrue(File.Exists(Path.Combine(fonts, "EBGaramond-Medium.ttf")), "font missing from " + fonts);
        string license = File.ReadAllText(Path.Combine(fonts, "OFL.txt"));
        StringAssert.Contains(license, "SIL OPEN FONT LICENSE");
        StringAssert.Contains(license, "EB Garamond");
        OnStaThread(() =>
        {
            var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(fonts + Path.DirectorySeparatorChar));
            Assert.IsTrue(families.Any(f => f.Source.Contains(MapRenderer.BundledFontFamily)), "the font file does not carry the family name the themes ask for");
        });
    }

    // ---- demo maps and the smoke render ----------------------------------------------------

    [TestMethod]
    public void DemoMaps_BuildThroughTheRealExport_AndAReplayRendersInEveryTheme()
    {
        string dir = TempDir();
        try
        {
            var env = AppEnvironment.Create(dir, null, null);
            string notes = DemoMap.Write(env);
            TestContext.WriteLine(notes);
            Assert.IsTrue(env.MapsExist);

            var maps = MapSet.Load(env.MapsDir, DemoMap.KeepId);
            var keep = maps.Get(DemoMap.KeepId)!;
            var cellars = maps.Get(DemoMap.CellarsId)!;
            Assert.AreEqual("Goblin Keep", keep.Name);
            Assert.AreEqual("Mossy Cellars", cellars.Name);
            Assert.AreEqual(2, keep.LayerCount, "courtyard level and terrace level");
            Assert.IsTrue(maps.Index.IsKnownMapId(DemoMap.CellarsId));

            // Every category is on show, every marker has a layer, and exactly one was placed by height.
            var treasures = keep.Markers.Concat(cellars.Markers).Where(m => !m.IsBonfire).ToList();
            foreach (string category in ItemCategories.All.Where(c => c != ItemCategories.Unknown))
                Assert.IsTrue(treasures.Any(m => m.Category == category), $"no demo item of category {category}");
            Assert.IsTrue(keep.Markers.Concat(cellars.Markers).All(m => m.Layer >= 0));
            Assert.AreEqual(1, treasures.Count(m => m.OffMesh));
            Assert.AreEqual(4, keep.Markers.Concat(cellars.Markers).Count(m => m.IsBonfire));

            // The walks follow the floor: stairs, bridge, the gate between the two maps.
            Assert.IsFalse(notes.Contains("straight line"), notes);

            OnStaThread(() =>
            {
                foreach (var theme in Theme.BuiltIn())
                {
                    foreach (var (script, view) in new[] { (DemoMap.WalkScript, "minimap"), (DemoMap.DescendScript, "full"), (DemoMap.ClimbScript, "fit") })
                    {
                        string png = Path.Combine(dir, $"{theme.Name}_{view}.png");
                        using var core = new TrackerCore(env, new ReplaySource(ReplayScript.Read(Path.Combine(dir, script))), "replay", startPump: false);
                        core.Renderer.Theme = Theme.Load(env.ThemesDir, theme.Name);
                        string summary = Headless.RenderReplay(core, png, view, 1.5, 960, 600, null);
                        TestContext.WriteLine(summary);

                        StringAssert.Contains(summary, "0 off the shown map");
                        StringAssert.Contains(summary, "largest distance to the located polygon: 0.00 m");
                        if (script == DemoMap.WalkScript)
                        {
                            StringAssert.Contains(summary, "map switches: 1, flag flips: 6, bonfire rests: 1");
                            StringAssert.Contains(summary, "final map: " + DemoMap.CellarsId + " Mossy Cellars");
                        }

                        // The picture is the size asked for and is not one flat colour.
                        var decoder = BitmapDecoder.Create(new Uri(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var frame = decoder.Frames[0];
                        Assert.AreEqual(960, frame.PixelWidth);
                        Assert.AreEqual(600, frame.PixelHeight);
                        var pixels = new byte[960 * 600 * 4];
                        new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 960 * 4, 0);
                        var colours = new HashSet<int>();
                        for (int i = 0; i < pixels.Length; i += 4 * 97)
                            colours.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
                        Assert.IsTrue(colours.Count > 20, $"{png}: only {colours.Count} colours");
                    }
                }
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- feedback tools ------------------------------------------------------------------

    [TestMethod]
    public void Feedback_MarkAppendsANote_AndTheExportHoldsLogsAndSettingsButNoMaps()
    {
        string dir = TempDir();
        try
        {
            var env = AppEnvironment.Create(Path.Combine(dir, "data"), null, null);
            DemoMap.Write(env);
            new TrackerSettings().Save(env.SettingsPath);

            using (var core = new TrackerCore(env, new ReplaySource(ReplayScript.Read(Path.Combine(env.DataDir, DemoMap.ClimbScript))), "replay", startPump: false))
            {
                for (int i = 0; i < 30; i++)
                    core.StepDirect(new DateTime(2026, 1, 1, 12, 0, 0).AddMilliseconds(40 * i));
                env.MarkFeedback(core.Session);
                env.MarkFeedback(core.Session);
            }
            string[] marks = File.ReadAllLines(env.FeedbackLogPath).Where(l => l.Contains(" mark ")).ToArray();
            Assert.AreEqual(2, marks.Length);
            StringAssert.Contains(marks[0], "map=" + DemoMap.KeepId);
            StringAssert.Contains(marks[0], "area=\"Goblin Keep\"");
            StringAssert.Contains(marks[0], "version=" + AppEnvironment.ShortVersion);
            StringAssert.Contains(marks[0], "world=(");

            string target = Path.Combine(dir, "desktop");
            Directory.CreateDirectory(target);
            string zipPath = env.ExportFeedback(target);
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            TestContext.WriteLine(string.Join(", ", names));
            CollectionAssert.Contains(names, "version.txt");
            CollectionAssert.Contains(names, "settings.json");
            CollectionAssert.Contains(names, "logs/feedback.log");
            Assert.IsFalse(names.Any(n => n.Contains("map.json") || n.Contains("navpoly") || n.StartsWith("maps/") || n.EndsWith(".bin")), "no maps in the feedback zip");
            using var reader = new StreamReader(zip.GetEntry("version.txt")!.Open());
            StringAssert.Contains(reader.ReadToEnd(), "Loot Goblins Map " + AppEnvironment.Version);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void AppEnvironment_KeepsEverythingUnderTheDataFolder()
    {
        string dir = TempDir();
        try
        {
            string data = Path.Combine(dir, "data");
            var env = AppEnvironment.Create(data, null, null);
            foreach (string path in new[] { env.MapsDir, env.CacheDir, env.LogsDir, env.SettingsPath, env.StatePath, env.ThemesDir, env.LiveLogPath, env.FeedbackLogPath })
                StringAssert.StartsWith(path, data + Path.DirectorySeparatorChar);
            Assert.IsFalse(env.MapsExist);
            Assert.IsFalse(env.ArchivesChangedSinceBuild(out _), "no game folder known: nothing to compare");

            // The build configuration points the builder at the data folder, never at the game folder.
            env.State.GamePath = Path.Combine(dir, "pretend game");
            var cfg = env.BuildConfig();
            Assert.AreEqual(env.MapsDir, cfg.ResolveOutDir());
            Assert.AreEqual(env.CacheDir, cfg.ResolveWorkDir());
            Assert.IsFalse(cfg.WriteLayerImages);

            // The orientation chosen in the debug window is remembered.
            env.SaveOrientation(new Orientation(true, false, 1));
            var again = AppEnvironment.Create(data, null, null);
            Assert.AreEqual(new Orientation(true, false, 1).Normalized(), again.BuildConfig().Orientation);
            Assert.IsFalse(string.IsNullOrEmpty(AppEnvironment.Version));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
