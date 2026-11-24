using System.Runtime.InteropServices;
using Memory.Allocators;

namespace Memory.Tests.Allocators;

public unsafe partial class PageAllocatorTests
{
    // Mirror boundary step size: 64 KB on Windows, 4 KB (PageSize) on Linux
    private static int MirrorGranularity =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? 64 * 1024
            : PageAllocator.SystemPageSize;

    private static int AlignToGranularity(int size)
    {
        int mask = MirrorGranularity - 1;
        return (size + mask) & ~mask;
    }

    [Fact]
    public void CreateMirroredBuffer_ShouldProvideBackToBackHardwareMirroring()
    {
        // Arrange: Request 1 stride boundary
        int requestedSize = MirrorGranularity;
        int stride = AlignToGranularity(requestedSize);
        void* nativeHandle = null;

        // Act
        byte* bufferStart = PageAllocator.CreateMirroredBuffer(requestedSize, out nativeHandle);

        try
        {
            // Assert foundational allocation properties
            Assert.True(
                bufferStart != null,
                "Kernel failed to return a valid mirrored buffer address pointer."
            );
            Assert.True(
                nativeHandle != null,
                "Kernel failed to emit a valid native tracking handle pointer."
            );

            // 1. Verify standard page-alignment of the base address block
            ulong address = (ulong)bufferStart;
            Assert.Equal(0UL, address & PageAllocator.PageAlignmentMask);

            // 2. Write validation signatures into the front of Mirror A
            bufferStart[0] = 0xDE;
            bufferStart[1] = 0xAD;

            // 3. Hardware Mirroring Verification: Check if Mirror B (exactly stride bytes away)
            // instantly surfaces the modifications out-of-band without software copying.
            Assert.Equal(0xDE, bufferStart[stride]);
            Assert.Equal(0xAD, bufferStart[stride + 1]);

            // 4. Inverse Verification: Mutate memory inside Mirror B
            bufferStart[stride] = 0xBE;
            bufferStart[stride + 1] = 0xEF;

            // 5. Check if Mirror A reflects the mutated byte flags instantly
            Assert.Equal(0xBE, bufferStart[0]);
            Assert.Equal(0xEF, bufferStart[1]);
        }
        finally
        {
            // Teardown: Clean up allocating maps cleanly
            if (bufferStart != null)
            {
                PageAllocator.FreeMirroredBuffer(bufferStart, requestedSize, nativeHandle);
            }
        }
    }

    [Theory]
    [InlineData(1)] // 1 byte -> forces rounding up to minimum granularity
    [InlineData(5000)] // 5000 bytes -> within first granularity window
    [InlineData(1048576)] // Exactly 1MB -> multiple windows
    public void CreateMirroredBuffer_ShouldRoundUpToNearestPageBoundaryCorrectly(int requestedBytes)
    {
        // Arrange
        void* nativeHandle = null;
        int expectedAlignedSize = AlignToGranularity(requestedBytes);

        // Act
        byte* bufferStart = PageAllocator.CreateMirroredBuffer(requestedBytes, out nativeHandle);

        try
        {
            Assert.True(bufferStart != null, "Allocation failed for requested test size.");

            // Verify that the mirror stride boundary snaps perfectly to the aligned step size
            bufferStart[0] = 0xA5;
            Assert.Equal(0xA5, bufferStart[expectedAlignedSize]);

            // Verify that we can write right up to the very last byte of the double virtual range safely
            long totalVirtualHorizon = (long)expectedAlignedSize * 2;
            bufferStart[totalVirtualHorizon - 1] = 0x5A;

            // Due to double-mirror wrap-around, the last byte of Mirror B updates the last byte of Mirror A
            Assert.Equal(0x5A, bufferStart[expectedAlignedSize - 1]);
        }
        finally
        {
            if (bufferStart != null)
            {
                PageAllocator.FreeMirroredBuffer(bufferStart, requestedBytes, nativeHandle);
            }
        }
    }

    [Fact]
    public void MirroredBuffer_ShouldAllowContinuousBlitAcrossBoundaryWithoutFaulting()
    {
        // Arrange: This test simulates a multi-threaded producer executing a single linear blit
        // that breaches the end of the buffer capacity ring boundary.
        int stride = MirrorGranularity;
        void* nativeHandle = null;
        byte* bufferStart = PageAllocator.CreateMirroredBuffer(stride, out nativeHandle);

        try
        {
            Assert.True(bufferStart != null);

            // Synthesize a continuous 4-byte mock payload on the stack frame
            byte[] srcArray = new byte[] { 0x11, 0x22, 0x33, 0x44 };

            // Set write cursor exactly 2 bytes BEFORE the stride wrap boundary
            byte* writeCursor = bufferStart + stride - 2;

            // Act: Execute a continuous copy that intersects the boundary.
            // 2 bytes write to the tail end of Mirror A, 2 bytes bleed past the boundary into Mirror B.
            fixed (byte* srcPtr = srcArray)
            {
                Buffer.MemoryCopy(srcPtr, writeCursor, 4, 4);
            }

            // Assert Structural Sanity:
            // Check that the tail end of Mirror A took the first two bytes
            Assert.Equal(0x11, bufferStart[stride - 2]);
            Assert.Equal(0x22, bufferStart[stride - 1]);

            // Check that the bleeding bytes hit the front edge of Mirror B cleanly
            Assert.Equal(0x33, bufferStart[stride]);
            Assert.Equal(0x44, bufferStart[stride + 1]);

            // Critical Check: Because Mirror B maps straight back to the same hardware backing frame,
            // the bytes that bled into Mirror B must automatically materialize at the front of Mirror A!
            Assert.Equal(0x33, bufferStart[0]);
            Assert.Equal(0x44, bufferStart[1]);
        }
        finally
        {
            if (bufferStart != null)
            {
                PageAllocator.FreeMirroredBuffer(bufferStart, stride, nativeHandle);
            }
        }
    }
}
