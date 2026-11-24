using Memory.Utilities;

namespace Memory.Tests.Utilities;

public class MemoryLayoutTests
{
    private readonly record struct SampleFixedHeader(long Id, int Flags);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    public void AlignTo4Bytes_ReturnsExpected(int bytes, int expected)
    {
        Assert.Equal(expected, MemoryLayout.AlignTo4Bytes(bytes));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 8)]
    [InlineData(8, 8)]
    [InlineData(9, 16)]
    public void AlignTo8Bytes_ReturnsExpected(int bytes, int expected)
    {
        Assert.Equal(expected, MemoryLayout.AlignTo8Bytes(bytes));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    public void AlignTo16Bytes_ReturnsExpected(int bytes, int expected)
    {
        Assert.Equal(expected, MemoryLayout.AlignTo16Bytes(bytes));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    public void AlignToCacheLine_ReturnsExpected(int bytes, int expected)
    {
        Assert.Equal(expected, MemoryLayout.AlignToCacheLine(bytes));
    }

    [Fact]
    public void AlignToCacheLine_ThrowsOnNegativeOrOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryLayout.AlignToCacheLine(-1));
        Assert.Throws<OverflowException>(() => MemoryLayout.AlignToCacheLine(int.MaxValue));
    }

    [Theory]
    [InlineData(0, 32, 0)]
    [InlineData(1, 32, 32)]
    [InlineData(33, 32, 64)]
    public void AlignUp_ReturnsExpected(int offset, int alignment, int expected)
    {
        Assert.Equal(expected, MemoryLayout.AlignUp(offset, alignment));
    }

    [Fact]
    public void GetAlignedAllocationSize_ComputesCacheAlignedTotalAndGuardsInvalidInputs()
    {
        int size = MemoryLayout.GetAlignedAllocationSize<SampleFixedHeader>(100);
        Assert.Equal(0, size % MemoryLayout.CacheLineBytes);
        Assert.True(size >= 116);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryLayout.GetAlignedAllocationSize<SampleFixedHeader>(-1)
        );
        Assert.Throws<OverflowException>(
            () => MemoryLayout.GetAlignedAllocationSize<SampleFixedHeader>(int.MaxValue)
        );
    }
}
