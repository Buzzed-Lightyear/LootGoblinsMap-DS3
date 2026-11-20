// Local stub replacing SoulsFormatsNEXT's Oodle native-DLL wrapper.
// DS3 uses zlib ("DFLT") DCX compression only; Oodle is never needed here,
// and the hard rules forbid prebuilt third-party binaries.
using System;

namespace SoulsFormats
{
    public static class Oodle
    {
        public enum OodleLZ_Compressor
        {
            OodleLZ_Compressor_Kraken = 8,
            OodleLZ_Compressor_Leviathan = 13,
        }

        public enum OodleLZ_CompressionLevel
        {
            OodleLZ_CompressionLevel_Optimal2 = 7,
        }

        public static byte[] Decompress(byte[] compressed, long uncompressedSize)
            => throw new NotSupportedException("Oodle compression is not supported in this build (not needed for DS3).");

        public static byte[] Compress(byte[] data, OodleLZ_Compressor compressor, OodleLZ_CompressionLevel level)
            => throw new NotSupportedException("Oodle compression is not supported in this build (not needed for DS3).");
    }
}
