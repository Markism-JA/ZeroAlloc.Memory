using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Memory.Utilities;

/// <summary>
/// Low-level unmanaged layout mechanics providing bare-metal CPU cache-line alignment math.
/// </summary>
public static class MemoryLayout
{
    public const int CacheLineBytes = 64;
    private const int CacheLineMask = CacheLineBytes - 1; // 63

    /// <summary> Rounds a byte count up to the nearest 4-byte boundary. </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignTo4Bytes(int bytes) => (bytes + 3) & ~3;

    /// <summary> Rounds a byte count up to the nearest 8-byte boundary. </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignTo8Bytes(int bytes) => (bytes + 7) & ~7;

    /// <summary> Rounds a byte count up to the nearest 16-byte boundary. </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignTo16Bytes(int bytes) => (bytes + 15) & ~15;

    /// <summary> Aligns an arbitrary byte count up to the nearest CPU cache-line boundary (64-byte word stride). </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignToCacheLine(int bytes)
    {
        if (bytes < 0)
            ThrowNegativeBytesException(nameof(bytes));
        if (bytes > int.MaxValue - CacheLineMask)
            ThrowOverflowException();

        return (bytes + CacheLineMask) & ~CacheLineMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignUp(int offset, int alignment)
    {
        int mask = alignment - 1;
        return (offset + mask) & ~mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int GetAlignedAllocationSize<TFixed>(int payloadBytes)
        where TFixed : unmanaged
    {
        if (payloadBytes < 0)
            ThrowNegativeBytesException(nameof(payloadBytes));

        int fixedSize = sizeof(TFixed);
        if (payloadBytes > int.MaxValue - fixedSize - CacheLineMask)
            ThrowOverflowException();

        int totalRawBytes = fixedSize + payloadBytes;
        return (totalRawBytes + CacheLineMask) & ~CacheLineMask;
    }

    [DoesNotReturn]
    private static void ThrowNegativeBytesException(string paramName) =>
        throw new ArgumentOutOfRangeException(paramName, "Byte length cannot be negative.");

    [DoesNotReturn]
    private static void ThrowOverflowException() =>
        throw new OverflowException(
            "Calculated memory allocation size exceeds maximum addressable integer bounds."
        );
}
