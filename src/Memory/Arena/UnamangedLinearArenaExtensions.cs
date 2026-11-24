using System.Runtime.CompilerServices;
using Memory.Utilities;

namespace Memory.Arena;

/// <summary>
/// Convenience extension methods for leasing typed cache-line aligned pointers and spans from an <see cref="UnmanagedLinearArena{T}"/>.
/// </summary>
public static unsafe class UnmanagedLinearArenaExtensions
{
    /// <summary>
    /// Type-safe lease returning a raw T* pointer aligned strictly to 64-byte CPU cache-line boundaries.
    /// Leverages <see cref="MemoryLayout.AlignToCacheLine"/> to guarantee SIMD readiness and zero cache straddling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T* LeasePointer<T>(this ref UnmanagedLinearArena<byte> arena, int count)
        where T : unmanaged
    {
        if (count <= 0)
            return null;

        int rawBytesNeeded = count * sizeof(T);
        int alignedBytesNeeded = MemoryLayout.AlignToCacheLine(rawBytesNeeded);
        byte* rawPtr = arena.Lease(alignedBytesNeeded);

        DebugMemoryGuard.PoisonBuffer(rawPtr, alignedBytesNeeded);

        return (T*)rawPtr;
    }

    /// <summary>
    /// Type-safe lease returning a zero-allocation <see cref="Span{T}"/> view for safe indexing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> LeaseSpan<T>(this ref UnmanagedLinearArena<byte> arena, int count)
        where T : unmanaged
    {
        T* ptr = arena.LeasePointer<T>(count);
        if (ptr == null)
            return Span<T>.Empty;

        return new Span<T>(ptr, count);
    }
}
