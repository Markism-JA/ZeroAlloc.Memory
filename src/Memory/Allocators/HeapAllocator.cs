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
