using System.Runtime.InteropServices;

namespace Memory.Collections;

/// <summary>
/// 64-byte cache-line padded monotonic sequence counter to prevent false sharing across CPU cores.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    /// <summary>The monotonic 64-bit sequence counter.</summary>
    [FieldOffset(0)]
    public long Value;
}
