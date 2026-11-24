using System.Runtime.InteropServices;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Lightweight 8-byte frame header for variable-length SPSC blocks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct SpscBlockHeader
{
    /// <summary>Byte length of the payload following this header.</summary>
    public int PayloadLength;

    /// <summary>Total 8-byte aligned footprint of header, payload, and padding.</summary>
    public int AlignedTotalSize;
}
