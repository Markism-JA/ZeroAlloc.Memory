using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Native.Posix;
using Memory.Native.Windows;
using Memory.Native.Windows.Enums;
#if NET6_0_OR_GREATER
using NativeUInt = nuint;
using NativeInt = nint;
#else
using NativeUInt = System.UIntPtr;
#endif

namespace Memory.Allocators;

/// <summary>
/// Provides high-performance, aligned unmanaged memory allocations directly on the C-heap.
/// Dispatches dynamically to .NET NativeMemory, Unity Persistent Allocator, or OS platform fallbacks.
/// </summary>
public unsafe struct HeapAllocator
{
    /// <summary>
    /// Allocates an aligned block of unmanaged memory on the C-heap.
    /// </summary>
    /// <param name="bytes">The number of bytes to allocate. Returns <c>null</c> if less than or equal to zero.</param>
    /// <param name="alignment">The desired byte alignment (normalized to a power of two at least <c>sizeof(void*)</c>).</param>
    /// <returns>A pointer to the allocated unmanaged block, or <c>null</c> if <paramref name="bytes"/> is non-positive or allocation fails.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Allocate(int bytes, int alignment)
    {
        if (bytes <= 0)
            return null;

        alignment = NormalizeAlignment(alignment);

#if NET6_0_OR_GREATER
        return NativeMemory.AlignedAlloc((NativeUInt)(uint)bytes, (NativeUInt)(uint)alignment);
#elif UNITY_5_3_OR_NEWER
        return Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(
            bytes,
            alignment,
            Unity.Collections.Allocator.Persistent
        );
#else
        return Allocate_Fallback(bytes, alignment);
#endif
    }

    /// <summary>
    /// Frees an unmanaged memory block previously allocated by <see cref="Allocate"/>.
    /// </summary>
    /// <param name="ptr">The pointer to free. No-op if <c>null</c>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Free(void* ptr)
    {
        if (ptr == null)
            return;

#if NET6_0_OR_GREATER
        NativeMemory.AlignedFree(ptr);
#elif UNITY_5_3_OR_NEWER
        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Free(
            ptr,
            Unity.Collections.Allocator.Persistent
        );
#else
        Free_Fallback(ptr);
#endif
    }

    /// <summary>
    /// Normalizes a requested byte alignment to a valid power of two at least <c>sizeof(void*)</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NormalizeAlignment(int alignment)
    {
        if ((alignment & (alignment - 1)) != 0 || alignment < sizeof(void*))
        {
            alignment = sizeof(void*);
        }
        return alignment;
    }

    #region Backward Compatibility & Fallback Allocator

    /// <summary>
    /// Platform-specific fallback allocator using <c>VirtualAlloc</c> on Windows or <c>posix_memalign</c> on POSIX.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void* Allocate_Fallback(int bytes, int alignment)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Kernel32.VirtualAlloc(
                null,
                (NativeUInt)(uint)bytes,
                WindowsMemAllocFlags.MEM_COMMIT | WindowsMemAllocFlags.MEM_RESERVE,
                WindowsPageProtection.PAGE_READWRITE
            );
        }
        else
        {
            void* ptr = null;
            int result = Libc.PosixMemalign(
                &ptr,
                (NativeUInt)(uint)alignment,
                (NativeUInt)(uint)bytes
            );

            return result == 0 ? ptr : null;
        }
    }

    /// <summary>
    /// Frees a block allocated via <see cref="Allocate_Fallback"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Free_Fallback(void* ptr)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Kernel32.VirtualFree(ptr, (NativeUInt)0, WindowsMemAllocFlags.MEM_RELEASE);
        }
        else
        {
            Libc.Free(ptr);
        }
    }

    #endregion
}
