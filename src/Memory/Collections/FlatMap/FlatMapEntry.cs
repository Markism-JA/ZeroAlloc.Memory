using System.Runtime.InteropServices;

namespace Memory.Collections.FlatMap;

/// <summary>
/// 64-bit aligned unmanaged key-value slot stored in contiguous native memory.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FlatMapEntry<TKey, TValue>
    where TKey : unmanaged
    where TValue : unmanaged
{
    public TKey Key;
    public TValue Value;
    public uint State; // 0 = Empty, 1 = Occupied
    private uint _padding;
}
