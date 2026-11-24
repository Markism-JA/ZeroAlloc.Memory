using Memory.Allocators;
using Memory.Layout;

namespace Memory.Tests.Layout
{
    public unsafe class UnmanagedVirtualPageTests
    {
        [Fact]
        public void Constructor_ShouldReserveAddressSpace_WithoutPhysicalRamCommitment()
        {
            // Reserve a massive structural chunk (e.g., 64 Megabytes)
            // This would be dangerous on standard heaps, but virtual reservations cost zero physical RAM pages.
            int virtualReservationSize = 64 * 1024 * 1024;

            using var virtualPage = new UnmanagedVirtualPage(virtualReservationSize);
            ulong address = (ulong)virtualPage.Carve(0, 1);
            ulong pageMask = (ulong)(PageAllocator.SystemPageSize - 1);

            // Assert base layout pointer alignment
            Assert.Equal(0UL, address & pageMask);
        }

        [Fact]
        public void CarveZeroed_ShouldInitializeMemoryToZero_OnFreshAllocation()
        {
            int pageSize = PageAllocator.SystemPageSize;
            using var virtualPage = new UnmanagedVirtualPage(pageSize);
            const int bytesNeeded = 128;

            // Act: Carve a zero-initialized virtual block
            void* zeroedPtr = virtualPage.CarveZeroed(bytesNeeded, 16);
            byte* bytePtr = (byte*)zeroedPtr;

            // Assert: Verify physical page mapping is zeroed
            for (int i = 0; i < bytesNeeded; i++)
            {
                Assert.Equal(0, bytePtr[i]);
            }
        }

        [Fact]
        public void CarveZeroed_ShouldWipePreviousGhostData_OnResetPage()
        {
            int pageSize = PageAllocator.SystemPageSize;
            using var virtualPage = new UnmanagedVirtualPage(pageSize);
            const int bytesNeeded = 128;

            // 1. Write dirty/ghost data using standard Virtual Carve
            void* rawPtr = virtualPage.Carve(bytesNeeded, 16);
            byte* bytePtr = (byte*)rawPtr;
            for (int i = 0; i < bytesNeeded; i++)
            {
                bytePtr[i] = 0xBB;
            }

            // 2. Reset the virtual page (offset returns to 0; physical memory mappings stay mapped and dirty)
            virtualPage.Reset();

            // 3. Act: Re-carve the recycled address window using CarveZeroed
            void* zeroedPtr = virtualPage.CarveZeroed(bytesNeeded, 16);
            byte* zeroedBytePtr = (byte*)zeroedPtr;

            // Assert: Addresses must match to confirm address window recycling
            Assert.True(
                rawPtr == zeroedPtr,
                "Addresses must match to verify virtual address recycling."
            );

            // Assert: Verify the stale 0xBB values have been thoroughly cleared to zero
            for (int i = 0; i < bytesNeeded; i++)
            {
                Assert.Equal(0, zeroedBytePtr[i]);
            }
        }

        [Theory]
        [InlineData(64, 64)]
        [InlineData(128, 64)]
        [InlineData(256, 128)]
        public void Carve_ShouldOnDemandCommit_AndAllowImmediateUserSpaceWrites(
            int bytesNeeded,
            int alignment
        )
        {
            using var virtualPage = new UnmanagedVirtualPage(PageAllocator.SystemPageSize * 2);
            // Skew the alignment register tracking value
            virtualPage.Carve(7, 1);

            // Act: Carve the targeted structural layout segment block
            void* committedSlicePtr = virtualPage.Carve(bytesNeeded, alignment);
            Assert.True(committedSlicePtr != null, "Kernel failed to carve memory slice.");

            // Validate strict alignment demands
            ulong addressValue = (ulong)committedSlicePtr;
            ulong alignmentMask = (ulong)(alignment - 1);
            Assert.Equal(0UL, addressValue & alignmentMask);

            // Act: Actively touch and write to the unmanaged physical boundary
            byte* rawAccessTracker = (byte*)committedSlicePtr;
            rawAccessTracker[0] = 0xDE;
            rawAccessTracker[bytesNeeded - 1] = 0xAD;

            // Assert: Physical memory reads back perfectly
            Assert.Equal(0xDE, rawAccessTracker[0]);
            Assert.Equal(0xAD, rawAccessTracker[bytesNeeded - 1]);
        }

        [Fact]
        public void Carve_ShouldThrowOutOfMemoryException_WhenVirtualWindowIsExhausted()
        {
            int pageSize = PageAllocator.SystemPageSize;

            using var virtualPage = new UnmanagedVirtualPage(pageSize);

            // Completely fill the allocation page boundary window
            virtualPage.Carve(pageSize, 1);

            // Attempting to carve even 1 single byte past the virtual threshold must trigger an OOM exception
            Assert.Throws<OutOfMemoryException>(() =>
            {
                virtualPage.Carve(1, 1);
            });
        }

        [Fact]
        public void Reset_ShouldRewindAllocationIndex_AllowingReCommitmentOverAddressSpace()
        {
            int pageSize = PageAllocator.SystemPageSize;

            using var virtualPage = new UnmanagedVirtualPage(pageSize);
            void* originalAddress = virtualPage.Carve(32, 16);

            // Act: Reset internal structural offset indexes back to zero frame lanes
            virtualPage.Reset();

            // Re-carving after a reset should return the exact same base pointer address window
            void* recycledAddress = virtualPage.Carve(32, 16);
            Assert.True(originalAddress == recycledAddress);

            // Verify write access operates seamlessly on recycled blocks
            int* intMemoryAccess = (int*)recycledAddress;
            intMemoryAccess[0] = 777;
            Assert.Equal(777, intMemoryAccess[0]);
        }
    }
}
