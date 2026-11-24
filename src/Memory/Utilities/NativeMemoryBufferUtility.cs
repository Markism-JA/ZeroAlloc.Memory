using System.Runtime.CompilerServices;

namespace Memory.Utilities;

public static unsafe class NativeMemoryBufferUtility
{
    /// <summary>
    /// Blits an unmanaged byte region using the fastest available engine or runtime primitive.
    /// Target Resolution: Unity Native C++ memcpy -> .NET Server SIMD Intrinsic -> BCL Buffer.MemoryCopy
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyMemory(void* destination, void* source, int byteCount)
    {
        if (byteCount <= 0 || destination == source)
            return;

#if UNITY_2021_3_OR_NEWER || UNITY_ENGINE
        // Direct native C++ memcpy binding inside Unity Engine / IL2CPP
        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(destination, source, byteCount);
#elif NET6_0_OR_GREATER
        // Zero-branch RyuJIT SIMD / ERMS intrinsic in .NET 6/8/9
        Unsafe.CopyBlockUnaligned(destination, source, (uint)byteCount);
#else
        // Pure BCL fallback (overlap-safe memmove)
        Buffer.MemoryCopy(source, destination, byteCount, byteCount);
#endif
    }

    /// <summary>
    /// Clears an unmanaged byte region to zero using the fastest available engine or runtime primitive.
    /// Target Resolution: Unity Native C++ memset -> .NET Server Memset Intrinsic -> Span.Clear
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ZeroMemory(void* ptr, int byteCount)
    {
        if (byteCount <= 0 || ptr == null)
            return;

#if UNITY_2021_3_OR_NEWER || UNITY_ENGINE
        // Direct native C++ memset binding inside Unity Engine / IL2CPP
        Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemClear(ptr, byteCount);
#elif NET6_0_OR_GREATER
        // Zero-branch RyuJIT memset intrinsic in .NET 6/8/9
        Unsafe.InitBlockUnaligned(ptr, 0, (uint)byteCount);
#elif NETSTANDARD2_1_OR_GREATER || NETCOREAPP2_1_OR_GREATER
        // Standard Span.Clear runtime memset primitive
        new Span<byte>(ptr, byteCount).Clear();

#else
        // Unrolled fallback for legacy runtimes
        byte* bytePtr = (byte*)ptr;

        while (byteCount >= 8)
        {
            *(ulong*)bytePtr = 0;
            bytePtr += 8;
            byteCount -= 8;
        }

        while (byteCount > 0)
        {
            *bytePtr = 0;
            bytePtr++;
            byteCount--;
        }
#endif
    }
}
