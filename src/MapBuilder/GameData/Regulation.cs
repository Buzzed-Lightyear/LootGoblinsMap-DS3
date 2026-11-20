using System.Security.Cryptography;
using System.Text;
using SoulsFormats;

namespace MapBuilder.GameData;

/// <summary>
/// The game's regulation: <c>Data0.bdt</c>, an AES-encrypted binder of param files. Read in place
/// (the file is opened read-only and decrypted in memory); nothing is written back.
/// Scheme and key as in SoulsFormats (<c>SFUtil.DecryptDS3Regulation</c>): AES-256-CBC, the first
/// 16 bytes are the IV, no padding.
/// </summary>
public sealed class Regulation
{
    public const string FileName = "Data0.bdt";

    private static readonly byte[] Key = Encoding.ASCII.GetBytes("ds3#jn/8_7(rsY9pg55GFN7VFL#+3n/)");

    private readonly Dictionary<string, byte[]> _params = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Param names in the regulation, without path or extension (for example "ItemLotParam").</summary>
    public IEnumerable<string> ParamNames => _params.Keys;

    public static Regulation Load(string gameDataDir)
    {
        string path = Path.Combine(gameDataDir, FileName);
        byte[] encrypted;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            encrypted = new byte[fs.Length];
            fs.ReadExactly(encrypted);
        }
        return FromEncrypted(encrypted);
    }

    public static Regulation FromEncrypted(byte[] encrypted) => FromBinder(Decrypt(encrypted));

    public static byte[] Decrypt(byte[] encrypted)
    {
        if (encrypted.Length < 32 || (encrypted.Length - 16) % 16 != 0)
            throw new InvalidDataException("Regulation file has an unexpected length for AES-CBC data.");
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 256;
        aes.BlockSize = 128;
        using var decryptor = aes.CreateDecryptor(Key, encrypted.AsSpan(0, 16).ToArray());
        return decryptor.TransformFinalBlock(encrypted, 16, encrypted.Length - 16);
    }

    /// <summary>Counterpart of <see cref="Decrypt"/>, used by tests to build a synthetic regulation.</summary>
    public static byte[] Encrypt(byte[] plain, byte[] iv)
    {
        byte[] padded = plain;
        if (plain.Length % 16 != 0)
        {
            padded = new byte[(plain.Length + 15) / 16 * 16];
            plain.CopyTo(padded, 0);
        }
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 256;
        aes.BlockSize = 128;
        using var encryptor = aes.CreateEncryptor(Key, iv);
        byte[] body = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        return iv.Concat(body).ToArray();
    }

    /// <summary>From the decrypted bytes: an optionally DCX-compressed BND4 with one file per param.</summary>
    public static Regulation FromBinder(byte[] decrypted)
    {
        byte[] binderBytes = DCX.Is(decrypted) ? DCX.Decompress(decrypted) : decrypted;
        if (binderBytes.Length < 4 || Encoding.ASCII.GetString(binderBytes, 0, 4) != "BND4")
            throw new InvalidDataException("Decrypted regulation is not a BND4 binder (wrong key or not a regulation file).");
        var bnd = BND4.Read(binderBytes);
        var regulation = new Regulation();
        foreach (var file in bnd.Files)
        {
            string name = Path.GetFileNameWithoutExtension((file.Name ?? "").Replace('\\', '/'));
            if (name.Length > 0)
                regulation._params[name] = file.Bytes;
        }
        return regulation;
    }

    public bool TryGetParam(string name, out ParamFile param)
    {
        if (_params.TryGetValue(name, out var bytes))
        {
            param = ParamFile.Read(bytes);
            return true;
        }
        param = null;
        return false;
    }

    public ParamFile GetParam(string name) =>
        TryGetParam(name, out var p) ? p : throw new InvalidDataException($"Regulation has no param named '{name}'.");
}
