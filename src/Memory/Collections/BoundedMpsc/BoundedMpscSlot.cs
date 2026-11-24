using System.Runtime.InteropServices;

namespace Memory.Collections.BoundedMpsc;

/// <summary>
/// Cache-friendly sequential slot holding a turnstile generation ticket and unmanaged payload value.
/// </summary>
/// <typeparam name="T">The unmanaged element type.</typeparam>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct BoundedMpscSlot<T>
    where T : unmanaged
{
    /// <summary>
    /// Turnstile ticket counter.
    /// In Vyukov's queue, this counter controls production and consumption rights for the slot across generations.
    /// </summary>
    public long Sequence;

    /// <summary>
    /// Contiguous unmanaged payload data (e.g., EventEnvelope). Zero framing metadata overhead.
    /// </summary>
    public T Value;
}
