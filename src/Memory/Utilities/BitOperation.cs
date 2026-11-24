using System.Runtime.CompilerServices;

namespace Memory.Utilities;

/// <summary>
/// Cross-target bit manipulation primitives with <c>System.Numerics.BitOperations</c> intrinsics on modern .NET
/// and branchless fallbacks on <c>netstandard2.1</c>.
/// </summary>
public static class BitOperation
{
    /// <summary>Rounds a 32-bit unsigned integer up to the next power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundUpToPowerOf2(uint value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return System.Numerics.BitOperations.RoundUpToPowerOf2(value);
#else
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
#endif
    }

    /// <summary>Rounds a 64-bit unsigned integer up to the next power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RoundUpToPowerOf2(ulong value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return System.Numerics.BitOperations.RoundUpToPowerOf2(value);
#else
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return value + 1;
#endif
    }

    /// <summary>Rounds a 32-bit unsigned integer down to the nearest power of two (or <c>0</c> if <c>0</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundDownToPowerOf2(uint value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return value == 0 ? 0 : 1u << (31 - System.Numerics.BitOperations.LeadingZeroCount(value));
#else
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value - (value >> 1);
#endif
    }

    /// <summary>Rounds a 64-bit unsigned integer down to the nearest power of two (or <c>0</c> if <c>0</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RoundDownToPowerOf2(ulong value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return value == 0 ? 0 : 1UL << (63 - System.Numerics.BitOperations.LeadingZeroCount(value));
#else
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return value - (value >> 1);
#endif
    }

    /// <summary>Counts the number of set bits (population count) in a 32-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(uint value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return System.Numerics.BitOperations.PopCount(value);
#else
        value -= (value >> 1) & 0x55555555u;
        value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
        return (int)(((value + (value >> 4)) & 0x0F0F0F0Fu) * 0x01010101u >> 24);
#endif
    }

    /// <summary>Counts the number of set bits (population count) in a 64-bit unsigned integer.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PopCount(ulong value)
    {
#if NETCOREAPP3_0_OR_GREATER || NET6_0_OR_GREATER
        return System.Numerics.BitOperations.PopCount(value);
#else
        value -= (value >> 1) & 0x5555555555555555UL;
        value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
        return (int)(((value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL) * 0x0101010101010101UL >> 56);
#endif
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is a positive power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(int value)
    {
#if NET6_0_OR_GREATER
        return System.Numerics.BitOperations.IsPow2(value);
#else
        return value > 0 && (value & (value - 1)) == 0;
#endif
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is a non-zero power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(uint value)
    {
#if NET6_0_OR_GREATER
        return System.Numerics.BitOperations.IsPow2(value);
#else
        return value != 0 && (value & (value - 1)) == 0;
#endif
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is a positive power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(long value)
    {
#if NET6_0_OR_GREATER
        return System.Numerics.BitOperations.IsPow2(value);
#else
        return value > 0 && (value & (value - 1)) == 0;
#endif
    }

    /// <summary>Returns <c>true</c> if <paramref name="value"/> is a non-zero power of two.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPow2(ulong value)
    {
#if NET6_0_OR_GREATER
        return System.Numerics.BitOperations.IsPow2(value);
#else
        return value != 0 && (value & (value - 1)) == 0;
#endif
    }
}
