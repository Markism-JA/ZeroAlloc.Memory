using Memory.Utilities;

namespace Memory.Tests.Utilities;

public class BitOperationTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 4u)]
    [InlineData(17u, 32u)]
    [InlineData(1024u, 1024u)]
    [InlineData(1025u, 2048u)]
    public void RoundUpToPowerOf2_UInt32_ReturnsExpected(uint input, uint expected)
    {
        Assert.Equal(expected, BitOperation.RoundUpToPowerOf2(input));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(3UL, 4UL)]
    [InlineData((1UL << 40) + 1UL, 1UL << 41)]
    public void RoundUpToPowerOf2_UInt64_ReturnsExpected(ulong input, ulong expected)
    {
        Assert.Equal(expected, BitOperation.RoundUpToPowerOf2(input));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 1u)]
    [InlineData(3u, 2u)]
    [InlineData(31u, 16u)]
    [InlineData(64u, 64u)]
    public void RoundDownToPowerOf2_UInt32_ReturnsExpected(uint input, uint expected)
    {
        Assert.Equal(expected, BitOperation.RoundDownToPowerOf2(input));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 1UL)]
    [InlineData((1UL << 40) + 12345UL, 1UL << 40)]
    public void RoundDownToPowerOf2_UInt64_ReturnsExpected(ulong input, ulong expected)
    {
        Assert.Equal(expected, BitOperation.RoundDownToPowerOf2(input));
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(0xF0F0F0F0u, 16)]
    [InlineData(uint.MaxValue, 32)]
    public void PopCount_UInt32_ReturnsSetBitCount(uint input, int expected)
    {
        Assert.Equal(expected, BitOperation.PopCount(input));
    }

    [Theory]
    [InlineData(0UL, 0)]
    [InlineData(1UL, 1)]
    [InlineData(0x5555555555555555UL, 32)]
    [InlineData(ulong.MaxValue, 64)]
    public void PopCount_UInt64_ReturnsSetBitCount(ulong input, int expected)
    {
        Assert.Equal(expected, BitOperation.PopCount(input));
    }

    [Fact]
    public void IsPow2_AllOverloads_HandleZeroNegativeAndPowersOfTwo()
    {
        Assert.False(BitOperation.IsPow2(0));
        Assert.False(BitOperation.IsPow2(-4));
        Assert.False(BitOperation.IsPow2(3));
        Assert.True(BitOperation.IsPow2(1));
        Assert.True(BitOperation.IsPow2(64));

        Assert.False(BitOperation.IsPow2(0u));
        Assert.True(BitOperation.IsPow2(1024u));

        Assert.False(BitOperation.IsPow2(0L));
        Assert.False(BitOperation.IsPow2(-8L));
        Assert.True(BitOperation.IsPow2(1L << 40));

        Assert.False(BitOperation.IsPow2(0UL));
        Assert.True(BitOperation.IsPow2(1UL << 63));
    }
}
