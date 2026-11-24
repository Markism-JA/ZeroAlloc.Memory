using Memory.Allocators;
using Memory.Utilities;

namespace Memory.Tests.Utilities;

public unsafe class NativeMemoryBufferUtilityTests
{
    [Fact]
    public void CopyMemory_CopiesExactBytesAndIgnoresZeroOrSelfCopy()
    {
        const int length = 256;
        byte* src = (byte*)HeapAllocator.Allocate(length, 64);
        byte* dst = (byte*)HeapAllocator.Allocate(length, 64);

        try
        {
            for (int i = 0; i < length; i++)
            {
                src[i] = (byte)(i ^ 0x5A);
                dst[i] = 0;
            }

            NativeMemoryBufferUtility.CopyMemory(dst, src, length);

            for (int i = 0; i < length; i++)
            {
                Assert.Equal(src[i], dst[i]);
            }

            // No-op paths should not throw or corrupt memory
            NativeMemoryBufferUtility.CopyMemory(dst, dst, length);
            NativeMemoryBufferUtility.CopyMemory(dst, src, 0);
            NativeMemoryBufferUtility.CopyMemory(dst, src, -16);
        }
        finally
        {
            HeapAllocator.Free(src);
            HeapAllocator.Free(dst);
        }
    }

    [Fact]
    public void ZeroMemory_ClearsTargetRangeAndPreservesAdjacentBytes()
    {
        const int total = 128;
        byte* buffer = (byte*)HeapAllocator.Allocate(total, 64);

        try
        {
            for (int i = 0; i < total; i++)
            {
                buffer[i] = 0xAA;
            }

            NativeMemoryBufferUtility.ZeroMemory(buffer + 16, 64);

            for (int i = 0; i < 16; i++)
                Assert.Equal(0xAA, buffer[i]);

            for (int i = 16; i < 80; i++)
                Assert.Equal(0, buffer[i]);

            for (int i = 80; i < total; i++)
                Assert.Equal(0xAA, buffer[i]);

            // No-op guards
            NativeMemoryBufferUtility.ZeroMemory(null, 64);
            NativeMemoryBufferUtility.ZeroMemory(buffer, 0);
            NativeMemoryBufferUtility.ZeroMemory(buffer, -1);
        }
        finally
        {
            HeapAllocator.Free(buffer);
        }
    }
}
