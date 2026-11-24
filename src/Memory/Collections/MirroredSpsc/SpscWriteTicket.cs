using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Memory.Collections.MirroredSpsc;

/// <summary>
/// Opaque value carrying pre-computed sequence offsets for two-phase zero-copy reservations.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct SpscWriteTicket
{
    public readonly long NewTail;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal SpscWriteTicket(long newTail) => NewTail = newTail;
}
