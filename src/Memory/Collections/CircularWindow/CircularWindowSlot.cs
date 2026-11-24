using System.Runtime.InteropServices;

namespace Memory.Collections.CircularWindow;

/// <summary>
/// Individual slot within the circular window carrying a monotonic sequence counter and unmanaged payload.
/// </summary>
/// <typeparam name="T">The unmanaged payload type.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CircularWindowSlot<T>
    where T : unmanaged
{
    /// <summary>
    /// Monotonic sequence written into this slot.
    /// -1L indicates uninitialized or in-flight mutation (seqlock dirty flag).
    /// </summary>
    public long Sequence;

    /// <summary>
    /// Stored unmanaged payload data.
    /// </summary>
    public T Value;
}
