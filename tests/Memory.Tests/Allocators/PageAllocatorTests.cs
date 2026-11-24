using Memory.Allocators;

namespace Memory.Tests.Allocators;

public unsafe partial class PageAllocatorTests
{
    [Fact]
    public void SystemProbe_ShouldInitializeValidPageSizeAndMask()
    {
        int pageSize = PageAllocator.SystemPageSize;
        ulong mask = PageAllocator.PageAlignmentMask;

        // Page size must be at least 4KB (4096) and a power of 2
        Assert.True(pageSize >= 4096, $"Detected invalid page size: {pageSize}");
        Assert.True((pageSize & (pageSize - 1)) == 0, "Page size must be a strict power of 2.");

        // Mask must equal pageSize - 1
        Assert.Equal((ulong)(pageSize - 1), mask);
    }

    [Theory]
    [InlineData(1)] // 1 byte (Extremely undersized, forces rounding up to 1 page)
    [InlineData(4096)] // Exactly 4KB
    [InlineData(10000)] // Misaligned size, forces rounding across multiple pages
    [InlineData(1024 * 1024)] // 1 Megabyte
    public void ReservedMemory_ShouldAlwaysBePageAligned(int requestedBytes)
    {
        void* ptr = null;
        try
        {
            // Act
            ptr = PageAllocator.ReserveAddressSpace(requestedBytes);
            Assert.True(ptr != null, "Kernel failed to reserve address space.");

            // Verify hardware page alignment
            ulong address = (ulong)ptr;
            ulong remainder = address & PageAllocator.PageAlignmentMask;

            Assert.Equal(0UL, remainder);
        }
        finally
        {
            // Teardown
            if (ptr != null)
            {
                PageAllocator.ReleaseVirtual(ptr, requestedBytes);
            }
        }
    }

    [Fact]
    public void VirtualMemory_FullLifecycle_ShouldExecuteSuccessfully()
    {
        // We request a size that sits exactly in the middle of a page
        int requestedBytes = 5000;
        int pageSize = PageAllocator.SystemPageSize;
        void* basePtr = null;

        try
        {
            // Phase 1: Reserve
            basePtr = PageAllocator.ReserveAddressSpace(requestedBytes);
            Assert.True(basePtr != null, "Reserve failed.");

            // Phase 2: Commit
            bool commitSuccess = PageAllocator.CommitVirtualRange(basePtr, requestedBytes);
            Assert.True(commitSuccess, "Commit failed.");

            // Phase 3: Hardware Read/Write Verification
            // Because the allocator rounds up to the nearest page boundary,
            // we are legally allowed to write all the way to the end of the aligned page frame.
            int alignedSize = (requestedBytes + (pageSize - 1)) & ~(pageSize - 1);

            byte* memoryArray = (byte*)basePtr;
            memoryArray[0] = 0xAA;
            memoryArray[alignedSize - 1] = 0xBB;

            Assert.Equal(0xAA, memoryArray[0]);
            Assert.Equal(0xBB, memoryArray[alignedSize - 1]);

            // Phase 4: Decommit
            bool decommitSuccess = PageAllocator.Decommit(basePtr, requestedBytes);
            Assert.True(decommitSuccess, "Decommit failed.");
        }
        finally
        {
            // Phase 5: Release (Guaranteed Anchor Pull)
            if (basePtr != null)
            {
                PageAllocator.ReleaseVirtual(basePtr, requestedBytes);
            }
        }
    }

    [Fact]
    public void Decommit_ShouldReturnFalse_WhenPassedUndersizedRange()
    {
        // In the Decommit method, we specifically bitwise-AND the byte count.
        // If the requested bytes to decommit is smaller than a system page,
        // it evaluates to 0 and must abort early to prevent kernel rejections.
        int undersizedBytes = PageAllocator.SystemPageSize - 1;

        // We pass a dummy pointer. If the guard clause works, it will return false
        // BEFORE attempting a native syscall that would otherwise crash.
        void* dummyPtr = (void*)0x12345678;

        bool result = PageAllocator.Decommit(dummyPtr, undersizedBytes);

        Assert.False(
            result,
            "Decommit should abort and return false if the range is less than one system page."
        );
    }
}
