// Local addition (not in upstream SoulSplitter): the single seam through which the vendored
// pointer code touches a process. It can only read. See PROVENANCE.md.

using System;

namespace SoulMemory.Memory;

/// <summary>A source of bytes at absolute addresses. Read-only by construction.</summary>
public interface IMemorySource
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>; false if any byte is unreadable.</summary>
    bool TryRead(long address, Span<byte> buffer);
}
