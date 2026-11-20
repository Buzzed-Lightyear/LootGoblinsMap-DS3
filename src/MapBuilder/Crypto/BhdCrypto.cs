using System.Numerics;

namespace MapBuilder.Crypto;

/// <summary>
/// Raw-RSA decryption of DS3 .bhd headers, matching the scheme used by UXM/BinderTool:
/// the file is a sequence of 256-byte big-endian blocks; each block is raised to the key's
/// exponent modulo the key's modulus, and the result is written as a 255-byte big-endian
/// block (left-padded with zeros). Implemented with System.Numerics only — no prebuilt
/// crypto libraries, per the hard rules.
/// </summary>
public static class BhdCrypto
{
    public static byte[] DecryptBhd(string bhdPath, string pemPublicKey)
    {
        var (modulus, exponent) = ParsePkcs1PublicKey(pemPublicKey);
        int inputBlockSize = (modulus.GetByteCount(isUnsigned: true));
        // The modulus is 2048-bit → 256-byte blocks in, 255-byte blocks out.
        int outputBlockSize = inputBlockSize - 1;

        byte[] input = File.ReadAllBytes(bhdPath);
        using var output = new MemoryStream(input.Length);
        byte[] block = new byte[inputBlockSize];
        for (int pos = 0; pos < input.Length; pos += inputBlockSize)
        {
            int n = Math.Min(inputBlockSize, input.Length - pos);
            Array.Clear(block);
            Array.Copy(input, pos, block, 0, n);
            var c = new BigInteger(block, isUnsigned: true, isBigEndian: true);
            var m = BigInteger.ModPow(c, exponent, modulus);
            byte[] outBlock = m.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (outBlock.Length > outputBlockSize)
                throw new InvalidDataException("RSA output block larger than expected; wrong key?");
            // Left-pad to the fixed output block size, as BouncyCastle/UXM do.
            byte[] padded = new byte[outputBlockSize];
            outBlock.CopyTo(padded, outputBlockSize - outBlock.Length);
            output.Write(padded, 0, padded.Length);
        }
        return output.ToArray();
    }

    /// <summary>Parses a PKCS#1 "BEGIN RSA PUBLIC KEY" PEM into (modulus, exponent).</summary>
    public static (BigInteger Modulus, BigInteger Exponent) ParsePkcs1PublicKey(string pem)
    {
        var b64 = string.Concat(
            pem.Split('\n')
               .Select(l => l.Trim())
               .Where(l => l.Length > 0 && !l.StartsWith("-----")));
        byte[] der = Convert.FromBase64String(b64);

        int pos = 0;
        ReadTag(der, ref pos, 0x30); // SEQUENCE
        var modulus = ReadDerInteger(der, ref pos);
        var exponent = ReadDerInteger(der, ref pos);
        return (modulus, exponent);
    }

    private static int ReadTag(byte[] der, ref int pos, byte expectedTag)
    {
        if (der[pos++] != expectedTag)
            throw new InvalidDataException($"DER: expected tag 0x{expectedTag:X2} at {pos - 1}, got 0x{der[pos - 1]:X2}.");
        int len = der[pos++];
        if ((len & 0x80) != 0)
        {
            int lenBytes = len & 0x7F;
            len = 0;
            for (int i = 0; i < lenBytes; i++)
                len = (len << 8) | der[pos++];
        }
        return len;
    }

    private static BigInteger ReadDerInteger(byte[] der, ref int pos)
    {
        int len = ReadTag(der, ref pos, 0x02); // INTEGER
        var value = new BigInteger(der.AsSpan(pos, len), isUnsigned: false, isBigEndian: true);
        pos += len;
        if (value.Sign < 0)
            throw new InvalidDataException("DER: unexpected negative INTEGER in RSA public key.");
        return value;
    }
}
