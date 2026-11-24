using System.Runtime.InteropServices;

namespace Memory.Collections.MonotonicTable;

/// <summary>
/// Unmanaged storage slot combining the value payload with its monotonic sequence scalar.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct MonotonicEntry<TValue>(in TValue value, ulong sequence)
    where TValue : unmanaged
{
    /// <summary>The stored unmanaged payload.</summary>
    public readonly TValue Value = value;

    /// <summary>The monotonic version/sequence scalar associated with this entry.</summary>
    public readonly ulong Sequence = sequence;
}
