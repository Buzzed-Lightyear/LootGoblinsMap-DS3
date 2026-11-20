using MapBuilder.Crypto;
using SoulsFormats;
using SoulsFormats.Cryptography;

namespace MapBuilder.Archive;

/// <summary>One Data#.bhd/.bdt pair, opened read-only in place.</summary>
public sealed class Ds3Archive : IDisposable
{
    public string Name { get; }
    public string BdtPath { get; }
    private readonly BHD5 _header;
    private readonly FileStream _bdt;

    private Ds3Archive(string name, string bdtPath, BHD5 header, FileStream bdt)
    {
        Name = name;
        BdtPath = bdtPath;
        _header = header;
        _bdt = bdt;
    }

    public static Ds3Archive Open(string gameDataDir, string name, string pemKey)
    {
        string bhdPath = Path.Combine(gameDataDir, name + ".bhd");
        string bdtPath = Path.Combine(gameDataDir, name + ".bdt");
        byte[] decrypted = BhdCrypto.DecryptBhd(bhdPath, pemKey);
        var header = BHD5.Read(decrypted, BHD5.Game.DarkSouls3);
        var bdt = new FileStream(bdtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new Ds3Archive(name, bdtPath, header, bdt);
    }

    public bool TryGetFile(string internalPath, out byte[] bytes)
    {
        bytes = null;
        uint hash = HashHelper.FromPathHash(internalPath);
        var bucket = _header.Buckets[(int)(hash % (uint)_header.Buckets.Count)];
        foreach (var fh in bucket)
        {
            if (fh.FileNameHash == hash)
            {
                byte[] padded = fh.ReadFile(_bdt);
                long size = fh.UnpaddedFileSize > 0 ? fh.UnpaddedFileSize : fh.PaddedFileSize;
                if (size < padded.Length)
                    Array.Resize(ref padded, (int)size);
                bytes = padded;
                return true;
            }
        }
        return false;
    }

    public void Dispose() => _bdt.Dispose();
}

/// <summary>All available Data archives of an install, resolved like the game: by path-hash lookup.</summary>
public sealed class ArchiveSet : IDisposable
{
    private readonly List<Ds3Archive> _archives = new();

    public static ArchiveSet OpenDarkSouls3(string gameDataDir)
    {
        var set = new ArchiveSet();
        foreach (var (name, key) in ArchiveKeys.DarkSouls3)
        {
            if (File.Exists(Path.Combine(gameDataDir, name + ".bhd")) &&
                File.Exists(Path.Combine(gameDataDir, name + ".bdt")))
            {
                set._archives.Add(Ds3Archive.Open(gameDataDir, name, key));
            }
        }
        if (set._archives.Count == 0)
            throw new FileNotFoundException($"No Data#.bhd/.bdt archives found in '{gameDataDir}'.");
        return set;
    }

    public IEnumerable<string> ArchiveNames => _archives.Select(a => a.Name);

    /// <summary>Looks the path up in every archive (later archives patch earlier ones, so last match wins,
    /// matching the game's load order preference for patch archives).</summary>
    public bool TryGetFile(string internalPath, out byte[] bytes, out string sourceArchive)
    {
        bytes = null;
        sourceArchive = null;
        foreach (var archive in _archives)
        {
            if (archive.TryGetFile(internalPath, out var b))
            {
                bytes = b;
                sourceArchive = archive.Name;
            }
        }
        return bytes != null;
    }

    public void Dispose()
    {
        foreach (var a in _archives)
            a.Dispose();
    }
}
