using System.Runtime.InteropServices;

namespace Memory.Collections;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedSequence
{
    [FieldOffset(0)]
    public long Value;
}
