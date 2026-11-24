using System.Runtime.InteropServices;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Lightweight 8-byte frame header for variable-length SPSC blocks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct SpscBlockHeader
{
    public int PayloadLength;
    public int AlignedTotalSize;
}
