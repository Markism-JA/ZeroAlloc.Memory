using System.Runtime.CompilerServices;
using Memory.Arena;

namespace Memory.Layout;

public static unsafe class VirtualPageCarveExtensions
{
    /// <summary>
    /// Carves a raw cache-aligned byte region from the virtual page and wraps it into an <see cref="UnmanagedLinearArena{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnmanagedLinearArena<byte> CarveScratchPad(
        this ref UnmanagedVirtualPage page,
        int byteCapacity,
        int alignmentRequirements = 64
    )
    {
        void* rawBuffer = page.Carve(byteCapacity, alignmentRequirements);
        return UnmanagedLinearArena<byte>.CreateOverPointer(rawBuffer, byteCapacity);
    }

    /// <summary>
    /// Carves a raw cache-aligned typed region from the virtual page and wraps it into an <see cref="UnmanagedLinearArena{T}"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UnmanagedLinearArena<T> CarveLinearArena<T>(
        this ref UnmanagedVirtualPage page,
        int count
    )
        where T : unmanaged
    {
        int totalBytes = count * sizeof(T);
        void* rawBuffer = page.Carve(totalBytes, alignmentRequirements: 64);
        return UnmanagedLinearArena<T>.CreateOverPointer(rawBuffer, totalBytes);
    }
}
