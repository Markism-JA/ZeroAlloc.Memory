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
    /// <summary>The unmanaged lookup key.</summary>
    public TKey Key;

    /// <summary>The unmanaged mapped value.</summary>
    public TValue Value;

    /// <summary>Slot occupancy flag (<c>0</c> = Empty, <c>1</c> = Occupied).</summary>
    public uint State; // 0 = Empty, 1 = Occupied
    private uint _padding;
}
