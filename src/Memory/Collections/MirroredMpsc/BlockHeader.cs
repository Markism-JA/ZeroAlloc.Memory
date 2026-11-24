using System.Runtime.InteropServices;

namespace Memory.Collections.MirroredMpsc;

/// <summary>
/// Represents the unmanaged 16-byte layout tracking header prefixing every data block
/// inside the <see cref="MirroredMpscRing"/>.
/// </summary>
/// <remarks>
/// Aligned to 8-byte boundaries so that 64-bit atomic/volatile operations on
/// <see cref="CommitSequence"/> never cross cache-line boundaries or cause unaligned faults on ARM64.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct BlockHeader
{
    public const int HeaderSize = 16;

    /// <summary>
    /// Gets or sets the exact byte length of the payload data succeeding this header.
    /// </summary>
    [FieldOffset(0)]
    public int PayloadLength;

    /// <summary>
    /// Gets or sets the 8-byte aligned total block footprint (header + payload + padding).
    /// </summary>
    [FieldOffset(4)]
    public int AlignedTotalSize;

    /// <summary>
    /// Monotonic sequence position stamped by the producer upon completing the memory copy.
    /// Replaces the legacy 0/1 IsCommitted flag to prevent reading stale lap memory.
    /// </summary>
    [FieldOffset(8)]
    public long CommitSequence;
}
