using System.Runtime.InteropServices;

namespace Memory.Collections.VirtualMap;

/// <summary>
/// Raw, unmanaged 64-bit aligned entry stored in demand-committed virtual memory.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct VirtualMapEntry<TKey, TValue>
    where TKey : unmanaged
    where TValue : unmanaged
{
    public TKey Key;
    public TValue Value;
    public uint State; // 0 = Empty, 1 = Occupied
    private uint _padding;
}
