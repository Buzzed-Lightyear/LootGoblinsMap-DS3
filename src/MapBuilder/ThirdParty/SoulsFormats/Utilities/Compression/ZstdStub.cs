// Local stub replacing SoulsFormatsNEXT's ZstdNet wrapper.
// DS3 uses zlib ("DFLT") DCX compression only; Zstd is never needed here,
// and the hard rules forbid prebuilt third-party binaries.
using System;

namespace SoulsFormats
{
    internal static class ZstdHelper
    {
        public static byte[] ReadZstd(BinaryReaderEx br, int compressedSize)
            => throw new NotSupportedException("Zstd compression is not supported in this build (not needed for DS3).");

        public static byte[] WriteZstd(byte[] data, int compressionLevel)
            => throw new NotSupportedException("Zstd compression is not supported in this build (not needed for DS3).");
    }
}
