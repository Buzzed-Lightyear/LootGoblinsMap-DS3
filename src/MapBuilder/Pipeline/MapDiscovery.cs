using MapBuilder.Archive;
using MapCore;

namespace MapBuilder.Pipeline;

/// <summary>A map found in the archives.</summary>
public sealed record DiscoveredMap(string Id, string MsbArchive, string NvaArchive, string NavmeshArchive)
{
    /// <summary>Only maps with both navmesh files can be drawn.</summary>
    public bool HasNavmesh => NvaArchive != null && NavmeshArchive != null;

    /// <summary>Served from one of the DLC archives.</summary>
    public bool Dlc => MsbArchive.StartsWith("DLC", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds the maps that exist on this install by asking the archives' hash tables for every
/// plausible <c>.msb</c> path. No fixed list: what the archives answer is what gets built.
/// Only hash lookups; no file data is read.
/// </summary>
public static class MapDiscovery
{
    public static List<DiscoveredMap> Find(ArchiveSet archives)
    {
        var maps = new List<DiscoveredMap>();
        // Area and block cover 00..99; the last two parts are 00 in every known map, 01 is probed for good measure.
        for (int area = 0; area < 100; area++)
            for (int block = 0; block < 100; block++)
                for (int region = 0; region < 2; region++)
                    for (int index = 0; index < 2; index++)
                    {
                        string id = MapIds.Format(area, block, region, index);
                        string msb = archives.FindArchive(Extractor.MsbPath(id));
                        if (msb == null)
                            continue;
                        maps.Add(new DiscoveredMap(id, msb,
                            archives.FindArchive(Extractor.NvaPath(id)),
                            archives.FindArchive(Extractor.NavmeshPath(id))));
                    }
        return maps;
    }
}
